# Experimental native extension metadata preflight

This is a bounded, **unfrozen**, schema-version-zero P6-04 slice. `schemas/pisharp-extension.schema.json` documents its structural form; `ExtensionManifestReader` implements stricter bounded admission and real artifact-byte verification. The contract profile is `experimental-native-manifest-0`. A passing result is **metadata only**: `IsLoadAuthorized` is always false and `RequiredBeforeLoading` records mandatory path, platform, ABI, dependency, entrypoint, broker, trust and loader prerequisites.

There is no assembly loading, entrypoint lookup/construction, module initializer invocation, process execution, installation, network access, executable discovery or grant of broker authority in these APIs. Ordinary directory/file metadata and reads are the only filesystem operations performed by production preflight. The host supplies an already owned `PiSharp.Contracts.JsonData` manifest and explicit package root; this slice does not scan project/user directories or import `.pi` resources.

## Manifest fields and closed profile

Every field below is required. Missing and explicit-null fields both reject; no unknown-field fallback is accepted. The original owned `JsonData` remains unchanged and is retained on successful parsing.

| Field | Experimental form |
| --- | --- |
| `schemaVersion` | Exact JSON integer token `0` |
| `id` | Lowercase bounded ASCII identifier, beginning/ending with a letter or digit |
| `packageVersion` | Three nonnegative, non-leading-zero Int32 components `major.minor.patch` |
| `hostApiRange` | Object containing inclusive `minimum` and exclusive `maximumExclusive`, with the same version grammar |
| `runtimeKind` | `native` |
| `assembly` | Package-relative `.dll` file path |
| `entryType` | Bounded ASCII namespace/nested type name; no generic entrypoint syntax |
| `tfm` | `net10.0` |
| `rids` | Nonempty unique explicit target list; recognized identifiers are `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` |
| `requiredFeatures` | Unique feature names, checked against the current experimental registration feature list |
| `declaredCapabilities` | Unique `tools`, `commands`, `observations` descriptor capabilities; wider capabilities reject explicitly |
| `resourcePaths` | Unique package-relative file paths; directory recursion is not implemented |
| `explicitOverrides` | Unique `{ kind: tool\|command, target }` declarations; every nonempty override list rejects until configuration/authorization broker integration exists |
| `artifactHashes` | Relative file path to 64-character SHA-256 hex; covers the assembly and every resource path |

The host API version defaults to `0.0.0`, representing the experimental profile rather than a stable SDK release. Versions do not yet accept prerelease/build strings or general npm/range syntax. Recognized RID metadata is not a supported-platform release claim. Capabilities declare requested metadata and do not grant filesystem, process, provider, UI, nested tool or session authority. The `tools`/`commands` forms currently describe P6-03 mock descriptor callbacks rather than actual Agent tools or session commands.

Additional hashed files may represent declared private/dependency assets, but this slice does not resolve `.deps.json` or prove dependency closure. Paths that differ only by case collide on every platform; assembly/resource references must use the exact spelling of their hash declaration. Raw JSON property order, whitespace and escapes remain owned unchanged. Typed SHA strings normalize hex case only; trust identity uses the original retained JSON.

## Explicit inspection trust

`Read(JsonData)` is pure metadata parsing and compatibility admission against the configured host profile. It returns a typed immutable compatible manifest, diagnostics and `ManifestValueSha256`: SHA-256 over UTF-8 of the retained root JSON value. This is not a hash of an arbitrary manifest file including external whitespace/BOM. `JsonData` already owns the parsed root; preflight never substitutes a reserialized/compacted root for trust identity.

`InspectAsync(metadata, packageRoot, sourceScope, effectiveScopeId, trustDecision, token)` requires an explicit caller/host `ExtensionTrustDecision`. Its only affirmative disposition is `ApproveMetadataInspection`. A decision is bound to canonical package root, retained-manifest-value hash, `User`/`Project`/`Explicit` source scope, bounded effective scope ID and host policy revision. The caller must derive effective scope identity and policy revision from its authoritative settings/trust context; this reader does not obtain consent, persist enablement or invent a trusted project.

No decision yields `TrustRequired`; explicit denial yields `TrustDenied`; any binding change yields `TrustBindingMismatch`. Changed capability/override declarations change the manifest identity. Changed effective scope, source scope, package root or policy revision cannot reuse the old affirmative inspection decision. Hash equality proves matching inspected bytes, not safety or permission. A matching digest never creates an affirmative decision.

Compatibility and lexical root validation precede inspection trust; trust precedes all artifact stream opens. Error diagnostics contain a fixed failure enum and bounded field/manifest-relative artifact path. They do not interpolate arbitrary filesystem exception messages or absolute private paths. Rejected preflights expose no partially verified artifact set.

## Path and read profile

Artifact paths are bounded relative paths using `/`. They reject absolute/drive/UNC/device forms, `.`/`..`, empty segments, backslash, ADS `:`, controls/NUL, surrogate code units, reserved device stems (including superscript COM/LPT aliases), wildcard characters and trailing space/dot segments. The absolute caller root follows a similarly conservative component profile and is normalized with `Path.GetFullPath` and removal of an ending separator. Lexically equivalent separator/trailing-separator roots share that identity. Root trust bindings compare exact casing on every OS because Windows directories may be case-sensitive; a case-only spelling change requires a new inspection decision. Hardlink, short-name and physical-file deduplication are not established.

Real filesystem verification currently runs only on Windows fixed local drives. UNC, mapped network drives, other drive types and non-Windows filesystem probing reject. On Unix, ordinary `File.GetAttributes` does not prove that a target is a regular file rather than a FIFO/device; this slice rejects before opening such targets. Cross-platform regular-file/no-follow identity support remains mandatory work.

The reader checks every existing root/artifact ancestor and target for `ReparsePoint`/`Device`, verifies directory/file shape and rejects missing files. Each declared file is opened read-only with `FileShare.Read`, hashed from real bytes sequentially and disposed asynchronously. All started reads and disposal are awaited; no detached tasks or unbounded event queue are created. Before/after component checks detect persistent replacement, but **do not establish atomic no-follow/open-handle identity or prevent transient symlink races**. Passing metadata cannot authorize loading, and the mandatory result gaps retain that distinction.

Defaults are at most 65,536 retained manifest UTF-16 characters, JSON depth 8, 128 artifacts, 64 resource files, 32 feature names, 16 RID names and 16 override declarations. Identifiers/features are at most 128 characters, entry type 512, relative paths 1,024 with 255 per component, and absolute root 4,096. Artifact bytes are bounded to 64 MiB per file and 128 MiB cumulatively per preflight. Limits are inclusive and configurable read limits may only lower the metadata/artifact-count ceilings. Concurrent calls are caller-owned; these are per-preflight limits rather than a whole-process heap limit.

Admission uses the existing strict retained-raw registration validator, so permissive `JsonData.FromElement` comments/root or nested trailing commas cannot bypass syntax checks. Duplicate properties, nonfinite numeric tokens, invalid Unicode, excessive depth, unknown fields and malformed shapes reject. Scalar metadata input fields reject controls; no action/argv/path/environment validator is relaxed.

The optional host `ExtensionArtifactReadObserver` observes real stream `Opened`/`Closing` ownership and cannot supply bytes. It is not an extension ABI callback or an execution capability. Its awaited closing callback receives `CancellationToken.None`; a nested `finally` still awaits stream disposal if that callback faults. After both closing and disposal settle, actual caller cancellation takes precedence over read or cleanup faults and propagates an `OperationCanceledException` carrying the caller's token, without an inner cleanup exception. Without caller cancellation, an `IOException` from closing still produces a sanitized `ArtifactReadFailed` denial with no verified artifacts or load authority; unexpected host callback exceptions retain their propagation policy. An uncooperative host observer can delay completion; this is cooperative cleanup, not forced termination.

## Evidence and remaining gates

The unchanged Phase6 plan's P6-04/P6-05/P6-08 requirements and the frozen P6-01 inventory were read before authoring. Inventory commit `1c4518e2037ea82ca917fb2e4f35a304a12bf8ae` records `compatibility/extensions/native-surface.json`, SHA-256 `11cc23450f834eab5c4d6dfe31b2e8e518be1bb64cdd69b6a9aa1f168b026b4f`, with all broader rows still required/HOLD. All 41 baseline events, flags/shortcuts/renderers, providers/MCP/virtual models, rich tools, context operations and their adapters remain required; rejecting them in this experimental profile does not close those rows.

Nine authored `ManifestAdmissionTests` groups use actual temporary artifact bytes and explicit task gates: byte hashing/tamper with a nonexecution boundary; explicit trust and binding invalidation; compatibility rejection before reads; retained JSON/Unicode/shape and inclusive limits; path/device/UNC/ADS/missing/reparse profiles; cancellation/read-fault cleanup; cancellation alone; noncanceled closing faults; and caller cancellation combined with closing faults. The three additional cleanup-composition groups cover cancellation during `Opened` and after the real read inside a held `Closing` callback. They assert that exclusive file access is denied while closing is held and succeeds after settlement, artifact bytes and owned manifest text remain unchanged, cancellation returns no admission, and noncanceled I/O faults retain sanitized denial. They also retain the unexpected noncanceled host-exception policy and constructor/marker nonexecution controls. Temporary directories are owned and checked before recursive fixture cleanup.

The cleanup-composition regression addresses independent finding `EXT-CANCEL-1`: a closing `IOException` previously replaced pending caller cancellation and was converted into `ArtifactReadFailed`. The final token check runs only after awaited closing and stream disposal, preserving ownership while restoring caller-cancellation precedence. These authored regressions require execution and independent review; their presence is not a passing gate claim.

The negative boundary controls use non-PE public `.dll` bytes and an authored constructor counter/marker absence. They do **not** qualify a real published plugin's module-initializer/constructor trust gate. The symlink fixture records `LinkFixtureGap` if the OS/account cannot create it; such a skip leaves reparse qualification mandatory. Non-Windows probing rejects explicitly. These authored controls, Windows profile behavior and all mandatory gaps need the parent's execution/review evidence; no build or tests are claimed by this document.

Still open: reviewed stable schema/ABI; directory discovery/order and physical-identity deduplication; archive extraction/traversal/symlink policy; immutable package/no-follow/handle binding; complete platform matrix; persistent scoped enable/disable/trust UI; real published loader and module-initializer denial witnesses; managed/native/RID dependency resolution and shared contracts; effective override/action/capability enforcement; session/UI/provider integration and release evidence.
