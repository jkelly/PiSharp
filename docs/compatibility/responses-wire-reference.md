# Genuine parsed Responses event reference capture

Three narrow traces execute unchanged Pi `processResponsesStream` at `d86654abb8862e201933517d6f1fce9f88dd117f`: completed interleaved text/tool output, EOF before a terminal response, and a completed response containing an unfinished tool call. The [input](../../fixtures/pi-v0.99.1/responses-wire/core.input.json) contains authored parsed provider DTOs, an explicit initial output and synthetic model rates. The [golden](../../fixtures/pi-v0.99.1/responses-wire/core.expected.json) contains only genuinely observed provider-hook events, normalized emission snapshots, final helper state and returned/thrown completion. No expected values were authored.

The input seam is `AsyncIterable<ResponseStreamEvent>` passed directly to the real exported helper. Type-only OpenAI SDK imports are erased by the locked Node runtime. The helper uses unchanged runtime imports and the already installed `partial-json` 0.1.7 package; no SDK, provider connection, key or further installation is needed. Each normalized event is cloned synchronously at `push` on the explicitly supplied real event-stream instance, then forwarded to its original method. The real awaited `onProviderStreamEvent` option records each raw DTO. Harness cleanup calls `stream.end()` after the helper finishes, without inventing a terminal event.

Measured results:

- The completed trace maps provider `output_index` 4 and 9 to normalized content indexes 0 and 1. Its eight normalized events interleave text and tool progress, complete the split argument escape, preserve authoritative text/arguments, encode the final text signature and retain the final tool namespace. The tool ID is `call_complete|fc_complete`.
- Final response ID is `resp_complete`, raw stop reason is `completed`, and normalized stop reason is `toolUse`. Input usage 20 minus cached 2 and cache-write 3 yields 15 uncached input tokens; output is 7, reasoning is 1 and total is 27. The recorded cost 34.5 follows explicitly authored synthetic unit rates and establishes no production catalog pricing.
- EOF emits two progress events, then throws `OpenAI Responses stream ended before a terminal response event`; the captured helper state remains pending.
- The unfinished-tool trace emits two progress events, then throws for missing `output_item.done`. Its provisional state already has `toolUse` and retains the scratch JSON buffer. The exception rejects the call; that provisional state does not authorize tool execution.

Across the three traces, 16 parsed DTOs produce 12 normalized emissions, with zero `start`, `done` or `error` events. Those envelope events and error conversion belong to the provider wrapper and are outside this helper capture. HTTP/SSE bytes and SDK decoding are also outside the input seam.

Initial capture ran twice in fresh credential-free homes/workspaces with byte-identical stdout. [responses-lock.json](../../tools/PiReferenceRunner/responses-lock.json) records the actual loaded closure: 20 upstream source files and two dependency files. Five additional source files are compared with canonical pinned Git bytes before execution: `openai-responses-shared.ts`, `constrained-sampling.ts`, `transform-messages.ts`, `hash.ts` and `sanitize-unicode.ts`. The remaining closure reuses qualified source/dependency pins. Checkout hashes and canonical Git blob hashes, the new harness and the unchanged loader/guard/strict JSON comparator are recorded. The existing [full-lock.json](../../tools/PiReferenceRunner/full-lock.json) environment remains unchanged.

Before and after runs, verification checks the clean source SHA/tree, Node executable/version, dependency projection and complete installed package trees. Capture children inherit only the explicit environment whitelist and fresh home/workspace paths. The existing guard blocks network and child-process builtins; this trusted-source harness is not an OS sandbox. Inputs supply timestamps and IDs directly; the helper invokes no clock or randomness.

The separate [manifest](../../fixtures/pi-v0.99.1/responses-wire/manifest.json) records requirement IDs, provenance and input/golden SHA-256 hashes. Default verification checks captured hashes before execution, compares two fresh runs byte-for-byte, verifies loaded closure and compares raw JSON with the frozen golden. Object key order is the only normalization; arrays, exact strings, missing/null, opaque fields and numeric lexemes remain significant. Duplicate properties and unsupported runtime precision fail explicitly. `--capture-new` refuses an existing golden, lock or manifest. Default mode writes separate actual output and a report under ignored `artifacts/responses-reference/`.

Run from the repository root with the locked Node executable:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/PiReferenceRunner/responses-run.mjs --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1'
```

The explicit read-only oracle path works from an isolated review clone. Without it, the runner uses the checkout's sibling oracle path. Both must match source/dependency/runtime pins. Native differential acceptance, provider wrapper/authentication, HTTP/SSE decoding, production catalogs/costs, reasoning/custom-tool variants, additional faults/platforms, all 45 mandatory Deferred rows and full provider/phase gates remain open. Shared scripts, status, manifest and earlier goldens are unchanged.
