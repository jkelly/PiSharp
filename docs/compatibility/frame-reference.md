# Genuine frame encoder/reducer reference capture

One small frame scenario is captured from unchanged public Pi source at `d86654abb8862e201933517d6f1fce9f88dd117f`. The harness calls the real `AssistantMessageFrameEncoder`, `reduceAssistantMessageFrames` and `parseStreamingJson` through the unchanged source resolver. This qualifies the recorded upstream behavior at one synthetic input seam. Production provider traffic, native differential acceptance and full phase gates require separate evidence.

The [authored input](../../fixtures/pi-v0.99.1/frame/interleaved-signed.input.json) interleaves three indexed blocks: thinking at index 0, text at index 1 and a tool call at index 2. Tool JSON arrives across an escape boundary. Authoritative end events replace draft content and stale signatures with final text, thinking and tool signatures, the tool namespace and complete arguments. Timestamps and response/tool IDs are explicit; the reducer invokes no clock.

The [genuine golden](../../fixtures/pi-v0.99.1/frame/interleaved-signed.expected.json) keeps raw event emission snapshots, encoded compact frames, prefix reductions, the final reduced message and the terminal result separate. Every emitted event is cloned immediately before later mutations of the live partial message. Measured results include:

- All three indexed blocks retain their final content and opaque signatures; the tool namespace and arguments remain exact.
- The first incomplete tool JSON fragment reduces to a display value with `path: "C:"`; the later fragment yields the complete path and nested arguments. This is observed upstream partial-JSON repair and does not authorize execution of partial arguments.
- Compact encoding omits the `done` event. Frame reduction keeps `stopReason: "pending"`; the separately recorded terminal result has `stopReason: "toolUse"`.
- The initial empty emission snapshot stays empty after the live partial message accumulates three blocks.

The capture ran twice in fresh credential-free homes/workspaces, with identical observations and loaded-module closure. Default verification then ran twice again and matched the frozen golden. The existing offline guard blocks network and child-process builtins during capture; no provider or tool is called. This trusted-source harness is not an OS sandbox.

[frame-lock.json](../../tools/PiReferenceRunner/frame-lock.json) hashes the five loaded modules: three upstream source files and two `partial-json` files. Upstream files also have canonical Git blob hashes. The lock pins the frame harness, reused loader/guard/strict JSON comparator and the existing [full-lock.json](../../tools/PiReferenceRunner/full-lock.json) environment. Before and after execution, verification checks the clean upstream SHA/tree, Node version and executable hash, the dependency projection and complete installed package trees (`partial-json` 0.1.7 and `typebox` 1.3.27). No dependency installation or upstream source edits occur.

The separate [manifest](../../fixtures/pi-v0.99.1/frame/manifest.json) records the requirement ID, authored-input versus genuine-output provenance, source SHA, clock/IDs and input/golden SHA-256 hashes. All fixture and harness checksums are verified before child execution. Object key ordering is the only normalization; array order, exact strings, missing/null, opaque fields and numeric lexemes remain significant. Duplicate properties and unsupported runtime numeric precision fail explicitly. `--capture-new` refuses an existing golden, manifest or lock; default mode writes actual output and a report under ignored `artifacts/frame-reference/` without changing expected output.

Run from the repository root using the locked Node executable:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/PiReferenceRunner/frame-run.mjs --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1'
```

The read-only `--oracle` path works from an isolated review clone and must match the locked source/dependency bytes. Without it, the runner uses the implementation checkout's sibling oracle directory. The report records two repeat runs, a golden match, source cleanliness and the six narrow checks above. The shared fixture manifest and all 45 mandatory Deferred rows remain unchanged. Additional frame scenarios, providers, platforms and full Phase 1/2/3 gates remain open.
