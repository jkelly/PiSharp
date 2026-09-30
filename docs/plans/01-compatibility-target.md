# Phase 1 Freeze the compatibility target

## Goal and boundary

Turn “port Pi” into a reproducible acceptance contract before implementation. The target is the coding-agent product at Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`, including dependencies needed for its stable behavior. PiSharp uses native C# plugins with an optional Node bridge; provider staging and release-profile exclusions remain proposals. This phase produces specifications and reference fixtures, not a working PiSharp agent.

Verified baseline facts: the coding-agent package identifies version 0.99.1; the repository root has a different package version. The repository license is MIT. The AI build normally regenerates model data, so source SHA alone is insufficient to freeze a reproducible provider catalog. [B1–B3]

## Entry prerequisites

- Access to the pinned repository, release/package artifacts and their dependency locks
- A planning owner empowered to approve compatibility exceptions and the supported OS matrix
- An isolated reference runner with a pinned Node runtime; .NET 10 development baseline and CI proposal
- No provider credentials or paid calls required; tests must use synthetic data and deterministic transports

## Work packages

1. **P1-01 Record the immutable baseline.** Owner: compatibility lead; proposed files: `compatibility/baseline.lock.json`, `docs/compatibility/baseline.md`. Verify tag resolution, commit, package versions and dependency graph. Hash source/package archives, lockfiles, runtime, generated catalogs and test assets; record origin and acquisition date. Use released catalogs where available. If unrecoverable, label a reconstructed catalog explicitly and obtain a scoped baseline decision. Output: independently verifiable baseline lock.

2. **P1-02 Establish provenance and notices.** Owner: release lead; files: `LICENSE`, `THIRD-PARTY-NOTICES.md`, `compatibility/provenance.json`. Retain the upstream MIT notice for copied/substantially adapted material; inventory assets, fonts, fixtures and transitive dependencies separately. Record source-to-port provenance and redistribution conditions. Check PiSharp naming independently of the code license. Output: notice inventory with unresolved items assigned, without treating MIT as a trademark grant. [B2]

3. **P1-03 Inventory observable surfaces.** Owner: subsystem leads; files: `compatibility/surfaces.json`, `compatibility/parity.json`. Enumerate commands/flags/exit codes, environment/config precedence, tools, events, SDK exports, sessions/migrations, RPC, extension hooks, resources, authentication, provider capabilities, MCP/codemode requirements and terminal behavior. Follow runtime dependencies, not just package names. Record experimental/separate products explicitly. Output: source-linked, individually testable requirement rows.

4. **P1-04 Approve release profiles and differences.** Owner: product/technical lead; files: `docs/decisions/0001-compatibility-scope.md`, `compatibility/profiles/*.json`. Separate behavioral, file/wire and bridge-source compatibility. Propose a limited preview plus native-v1 profile; mark each row Supported, Intentionally different, Deferred or Out of scope, alongside mandatory status and evidence state. A mandatory Deferred row blocks a full-parity claim. Record safer trust defaults and isolated PiSharp storage as intentional differences where applicable.

5. **P1-05 Freeze cross-phase seams.** Owner: architecture lead; files: `docs/contracts/{messages,events,tools,sessions,rpc,extensions}.md`, `schemas/`. Specify discriminators, casing, null/absence, IDs, timestamps, unknown fields, version negotiation and state ownership. Separate native C# contracts from Pi wire DTOs. Consult P3–P7 owners before freezing ordering/cancellation assumptions. Output: contract review and representative JSON examples, with open decisions identified rather than guessed.

6. **P1-06 Build the offline reference runner.** Owner: compatibility/testing lead; paths: `tools/PiReferenceRunner/`, `tests/PiSharp.Compatibility.Tests/`. Run unmodified pinned behavior through a thin harness; inject fake provider transport, clock, IDs and deterministic tools at supported seams. Isolate home/workspace, disable credentials/network, capture outgoing requests, ordered events, terminal result, stdout/stderr and filesystem effects. Snapshot mutable upstream partials at emission time. Output: reproducible oracle with runner/version metadata.

7. **P1-07 Author and qualify the corpus.** Owner: test lead; paths: `fixtures/pi-v0.99.1/{providers,agent,sessions,rpc,extensions,terminal}/`. Build sanitized fixtures from upstream tests and synthetic cases; associate every mandatory requirement with a fixture or scheduled test. Capture reference outputs now; port assertions land in their owning phases. Include normal, interruption, malformed-input and restart cases. Output: fixture manifest, checksums, expected traces and explicit coverage gaps.

8. **P1-08 Gate changes to the baseline.** Owner: CI/release lead; files: `tools/CompatibilityReport/`, `docs/compatibility/change-policy.md`. Validate IDs/links/checksums and prevent unreviewed golden regeneration. Run the reference corpus repeatedly in clean environments, compare outputs and inject deliberate mutations to prove the comparator fails. Publish separate expected/actual diffs and exception records. Output: signed-off Phase 1 baseline and contract handoff.

## Fixture and normalization contract

Each fixture records requirement IDs, source SHA, scenario, fake clock/seed, environment/OS, input transcript, provider wire frames or tool script, expected ordered events/final result/effects, normalizer version and provenance. Start with text, interleaved reasoning/tools, tool errors, steering/abort, branching/compaction, RPC correlation/malformed lines and extension cancellation. Later phases expand breadth.

Canonicalization is allowlisted: replace temporary roots and declared volatile IDs bijectively; preserve identity relationships; normalize clock origins without erasing ordering. Sort JSON object keys only where ordering has no semantics. Never sort arrays/events, flatten content, strip signatures, change missing to null, suppress errors, round all numbers or normalize away tool execution order. Preserve exact strings/opaque payloads and monetary/token semantics. Network byte fragmentation may vary in dedicated transport tests; observable delta coalescing requires a separately named equivalence profile. Raw and normalized traces remain available. Explicitly test CRLF, Unicode, escaped paths, duplicate IDs and false-positive normalization.

## Exit gate and evidence

Baseline hashes verify; reference runs are deterministic; comparator mutation tests fail as intended; every mandatory row has an owner, test ID and target phase; unknown scope items have decisions or named blockers. License/provenance review and P2–P7 contract review are recorded. No claim of PiSharp conformance is made from reference-only results.

## Dependencies and effort

P1-01 precedes reference capture. P1-02 and P1-03 run in parallel; P1-04 depends on inventory; P1-05 and P1-06 can overlap once seams are understood; P1-08 follows corpus qualification. P2 contract design and later-phase test design can begin before the gate, but frozen expectations must precede parity assertions. Indicative human-equivalent effort: 10–18 engineer-days, excluding unavailable artifacts or scope decisions; this is a planning range, not a delivery promise.

## Principal risks and decisions

Changing catalogs, unpublished artifacts and undocumented behavior can make a tag insufficient. Resolve through hashed artifacts and explicit deviations. Decide required operating systems, exact native-v1 scope and catalog provenance before claiming the baseline frozen. Never silently retarget to main.

## Sources

All references below are pinned to the compatibility commit. Facts are sourced; project/file names, task splits, gates and estimates are proposals.

- **B1** [Coding-agent package version and dependencies](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/package.json) and [root package metadata](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/package.json)
- **B2** [MIT license](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE)
- **B3** [AI build and catalog scripts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/package.json)
- **B4** [AI types and event protocol](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/types.ts)
- **B5** [Provider and models runtime](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/models.ts)
- **B6** [Cross-provider message transformation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/api/transform-messages.ts) and [transcript normalization](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/transcript.ts)
- **B7** [Assistant message frames and reduction](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/assistant-message-frame.ts) and [pre-generation authentication tests](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/test/pre-generation-error.test.ts)
- **B8** [Streaming JSON parsing and repair](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/json-parse.ts)
- **B9** [Responses terminal-event tests](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/test/openai-responses-terminal-event.test.ts) and [provider test inventory](https://github.com/earendil-works/pi/tree/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/test)
