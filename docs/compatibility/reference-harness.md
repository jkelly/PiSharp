# Offline reference and authored contract evidence

The [fixture manifest](../../fixtures/pi-v0.99.1/manifest.json) contains four newly authored frame-replay contracts and three captured upstream queue oracles. Each identifies the source SHA, requirement IDs, scenario, fixed clock/seed, environment, normalizer, provenance and SHA-256 of input and expected output. Authoring and oracle directories remain separate.

`providers/*.json` supplies upstream-shaped compact frames and a terminal message to the native fixture harness. Its expected event/result projections are authored synthetic expectations. These files exercise indexed interleaving, authoritative ends, signatures, empty redacted thinking, usage zero versus absence, exact strings and pre-start errors. They do not contain recorded provider wire frames and do not prove protocol or upstream frame-reducer parity.

`reference-inputs/*.json` supplies synthetic operations to the exact unmodified `packages/ai/src/utils/event-stream.ts`. Node's built-in TypeScript stripping imports this dependency-free runtime module. Captures under `reference-oracle/` prove only queue ordering, first terminal settlement, error before start and shared live partial references. Emission snapshots and delayed-consumer observations are recorded separately: the latter can show later partial mutation, so full provider capture must snapshot at emission time.

The runner verifies checkout SHA/cleanliness, the source module's checkout bytes and the exact Node binary hash. Every fixture runs twice in a fresh workspace/home under ignored `artifacts/reference-temp`; credential environment variables are not inherited. A child guard prohibits network and child-process builtins. The selected source module has no runtime package imports. This is a deterministic dependency-free seam, not a general hostile-code sandbox. No credentials, network calls, paid providers, npm lifecycle scripts or dependency installation are used.

From the repository root:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/PiReferenceRunner/run.mjs --upstream 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-upstream-v0.99.1' --out-dir artifacts/reference
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' --test tools/CompatibilityReport/comparator.test.mjs
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/CompatibilityReport/compare.mjs fixtures/pi-v0.99.1/providers/text-authoritative-end.expected.json artifacts/native/text-authoritative-end.actual.json
```

Raw JSON comparison sorts object keys only. It preserves array/event order, exact decoded strings, null versus absence, unknown/opaque fields and numeric lexemes. Duplicate names and malformed JSON are rejected. Thus large integers and high-precision decimals cannot collapse during comparison; number formatting differences also count as differences. The runtime harness parses only inputs whose decimal values survive JavaScript parse/stringify without change and rejects unsupported precision explicitly. Mutation tests exercise ordering, signatures, Unicode normalization, CRLF, IDs, missing/null, token zero/absence and precise numbers.

Full upstream frame reduction is blocked by absent `partial-json`; provider/agent/session/RPC/extension/terminal execution needs additional pinned dependencies and artifacts. The current runner records no outgoing provider request, stdout/stderr product behavior or filesystem tool effects. Those remain future fixture families. Matching current files supports only their stated narrow evidence scope.
