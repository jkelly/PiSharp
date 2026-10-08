# Genuine streaming JSON preview reference corpus

The first corpus captures 39 inputs across 13 categories using unchanged Pi `parseStreamingJson` at `d86654abb8862e201933517d6f1fce9f88dd117f` and its upstream-locked `partial-json` 0.1.7 dependency. Inputs are authored; every expected result is measured from the real parser. No upstream source, dependency, shared harness or earlier golden changes.

[core-preview.input.json](../../fixtures/pi-v0.99.1/json-preview/core-preview.input.json) covers undefined/empty/whitespace, strict objects/arrays/scalars, incomplete containers/strings/literals/numbers, nesting, split escapes, partial Unicode escapes, malformed fallback and string repair. [core-preview.expected.json](../../fixtures/pi-v0.99.1/json-preview/core-preview.expected.json) records the exact input text, observed result kind and value, plus a separate strict-runtime input check. The corpus invokes the parser independently on each prefix; it has no clock, randomness, provider traffic or executable tool calls.

Measured behavior includes:

- Strict arrays and scalar strings, booleans, numbers and null retain their shapes. Incomplete arrays retain completed items; unfinished nested objects retain completed structure and partial strings.
- `tru` and `fal` yield booleans, `nu` yields `{}`, and strict `null` yields null. `12e+` yields 12; `12.` yields `{}` in this pinned runtime.
- A trailing split backslash leaves the path preview `C:`. The completed escape restores the full path. An unfinished Unicode escape drops its incomplete portion; a completed high surrogate before another unfinished escape is retained as an escaped lone surrogate in JSON.
- Malformed tokens fall back to `{}`; a malformed object tail retains its prior completed property. Raw controls and invalid string escapes are repaired by the unchanged parser.

Numeric probes expose the JavaScript precision limit explicitly. `9007199254740993` is observed as `9007199254740992`; the two distinct long decimal inputs both yield `0.12345678901234568`. Original input lexemes remain separate and the strict runtime-input checker rejects all three as unsupported precision. The exactly representable large integer is accepted. Negative zero and overflow are measured as `-0` and `Infinity` in an explicit `runtimeOnlyNumber` field, with no JSON result, because ordinary JSON serialization would erase those distinctions. These probes characterize upstream runtime behavior and do not establish lossless numeric parity. No numeric-limit case qualifies native acceptance.

The initial capture ran twice in fresh credential-free homes/workspaces and matched exactly. [json-preview-lock.json](../../tools/PiReferenceRunner/json-preview-lock.json) records the loaded closure: the unchanged source resolver and JSON parser, plus two `partial-json` modules. It pins checkout and canonical Git blob source hashes, the new capture/runner, reused loader/offline guard/strict JSON comparator and the existing [full-lock.json](../../tools/PiReferenceRunner/full-lock.json) environment. Source SHA/tree cleanliness, the locked Node executable, dependency projection and complete installed package trees are checked before and after execution. The offline guard prohibits network and child-process builtins during capture; this trusted-source harness is not an OS sandbox.

The separate [manifest](../../fixtures/pi-v0.99.1/json-preview/manifest.json) records source SHA, requirement ID, provenance and input/golden SHA-256 hashes. Default verification checks all recorded harness, loaded-source and fixture hashes before execution, runs twice, compares repeats, then compares with the frozen golden. Only object key order is normalized; arrays, strings, null/missing, opaque fields and numeric lexemes remain significant. Duplicate property names and unsupported numeric precision in ordinary runtime evidence fail explicitly. Actual output and the report are written under ignored `artifacts/json-preview-reference/`. `--capture-new` refuses an existing golden, manifest or lock.

Run from the repository root using the locked Node executable:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/PiReferenceRunner/json-preview-run.mjs --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1'
```

The explicit read-only oracle path works from an isolated review clone; without it the runner uses the implementation checkout's sibling oracle directory. Either path must match locked source/dependency bytes. This upstream-only corpus leaves native preview implementation and differential acceptance, provider behavior, additional adversarial inputs, platform coverage, all 45 mandatory Deferred rows and full phase gates open. The shared fixture manifest remains unchanged.
