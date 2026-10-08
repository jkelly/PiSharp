# Genuine public RPC JSONL framing reference

Root first execution completed:24 authored reader cases expanded to85 actual stream probes/126 observed callbacks, plus six serializers. Two fresh guarded children returned byte-identical captures; source remained clean. Golden SHA-2563d283bdcb705937e25685a04355d300fa61683b5312670396233066d2789f6e0; lock9aefa6ac9a9e774af35d1b5bfdf7508e32c26c7944710e8e1dc73fbb3df41d7b. This is upstream observation only. Native comparison and independent provenance qualification remain pending; no full RPC gate is closed.

The [harness](../../tools/PiReferenceRunner/capture-rpc-jsonl.mjs) calls the unchanged whole `packages/coding-agent/src/modes/rpc/jsonl.ts` at Pi commit `d86654abb8862e201933517d6f1fce9f88dd117f`. That canonical module is 1,503 bytes, SHA-256 `95723d349fcebad1f1da7ce103d02ba7d5e2c876b7d178d41d8b56beedbd93e0`. It exports `attachJsonlLineReader` and `serializeJsonLine`; no private function is extracted, import erased, source changed or line splitter copied.

The module imports only `node:string_decoder` at runtime. Its `node:stream` import is type-only. The harness supplies actual Node `Readable.from` streams with authored buffer or string chunks. A forwarding resolution/load observer admits the one pinned source module and Node builtins, retaining the actual module pin and its source-origin builtin resolution. Existing hash-pinned `offline-guard.mjs` blocks network and child-process APIs in each capture child. No package installation, external package or provider operation is required.

The [input](../../fixtures/pi-v0.99.1/rpc-jsonl/core.input.json), SHA-256 `8a5388aa9bb953beed7410044370a500e87dfd20644904d1ef6a93cddbeb5bb7`, has 24 authored reader cases and six serializer values. Three reader cases specify every possible two-buffer split, including the empty leading/trailing buffer boundaries. Other cases deliver every byte independently or specify direct string/buffer chunks. Fragment construction supplies transport inputs and contains no expected splitting logic.

The bounded reader corpus includes LF and CRLF, empty lines, bare and repeated CR, complete/incomplete final EOF tails, U+2028/U+2029, emoji and pi, escaped LF versus raw LF inside quoted text, BOM, duplicate keys, escaped lone surrogates, invalid/truncated UTF-8, raw wide numeric text and whitespace. Two cases call the actual returned cleanup function after an authored data boundary. The serializer corpus covers Unicode/control characters, null, ordered arrays, a top-level string, safe finite numbers and integer-like object property names.

## What the capture records

Each reader probe retains the exact authored chunk sequence, callback strings and their UTF-8 hashes, whether each callback ran during data or EOF delivery, per-chunk callback counts and listener counts before/after attaching or calling the cleanup function. Empty or malformed callback strings remain present. Two consecutive cleanup calls and final cleanup are recorded rather than replaced with an authored result.

Each serializer observation retains the exact string returned by the source export, its UTF-8 hex, byte count and SHA-256. JavaScript materializes these authored safe JSON values before calling the export. This observes the source's `JSON.stringify` serialization, including its object-property ordering; it does not establish arbitrary-precision serialization or raw numeric token preservation by that export.

No JSON parser is called on emitted reader lines. Malformed text, duplicate keys, BOM and escaped lone surrogates are framing inputs, not claims of RPC JSON admission. Invalid or truncated UTF-8 records the pinned Node `StringDecoder` behavior. Direct string and mixed buffer/string cases exercise the public union-typed data path; they do not claim that real stdin emits such string chunks. Native UTF-8, duplicate-key, object-shape or surrogate rejection is a separately disclosed admission/hardening comparison.

The helper's returned callback strings and serializer outputs remain distinct observations. There is no RPC dispatcher, command correlation, application process, JSON object parser, stdin/stdout transport, write/flush/backpressure, cancellation, frame/depth bound or full RPC phase acceptance in this capture. The canonical `rpc.framing` requirement remains open. This additional source-module pin does not extend the independently qualified baseline source-file inventory.

## Initial capture and immutable reproduction

Use the installed qualified Windows Node v24.19.0 executable, SHA-256 `3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237`. Root performs initial creation after reviewing the three candidate files:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  tools/PiReferenceRunner/capture-rpc-jsonl.mjs --capture-new `
  --upstream 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1\upstream'
```

The parent verifies the approved source checkout's pinned HEAD/tree, clean status, actual module bytes and canonical Git blob before and after two fresh children. The children run with explicit native type stripping, the unchanged offline guard and credential-free owned temporary homes/workspaces. No clocks, RNG APIs or data fields are normalized. Cleanup checks each resolved temporary directory stays beneath its owned scratch root before deletion.

Only a successful, byte-identical pair creates `core.expected.json`, `capture-1.raw.json`, `capture-2.raw.json`, `oracle.lock.json` and `manifest.json`, using exclusive creation. An existing or partial evidence set is preserved and rejected by `--capture-new`. The lock/manifest retain actual runtime/module/harness/input/golden/raw-pair hashes, source checks, empty external dependency closure and initial capture history. There is no successful-capture claim merely from preparing this harness and authored input.

Default execution uses the same command without `--capture-new`. It verifies the frozen input/golden/lock/runtime/harness/source/raw-pair pins before starting children, captures twice again and compares fresh callbacks and serializer output with the initial evidence. Review output goes only to `artifacts/rpc-jsonl-reference`. It does not overwrite accepted fixture evidence. An explicit `--upstream` works from a detached candidate repository but must resolve to the exact approved source checkout. Duplicate, unknown or incomplete command arguments fail.

The raw comparator ignores only object-key order. Callback strings retain raw JSON-looking numeric text, missing versus null distinctions, Unicode and array order; they are never decoded into runtime JSON objects. This framing evidence is suitable for bounded native comparisons while leaving JSON admission, writer transport and dispatcher acceptance to their respective work.
