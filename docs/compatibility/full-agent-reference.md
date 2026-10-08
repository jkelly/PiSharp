# Genuine awaited agent-loop reference capture

One narrow full awaited Pi `runAgentLoop` scenario is captured from unmodified public source at `d86654abb8862e201933517d6f1fce9f88dd117f`. It uses the unchanged upstream source resolver and approved task-local `typebox` 1.3.27 / `partial-json` 0.1.7 installation. This advances beyond earlier queue-only probes. It does not qualify production provider wire behavior, native differential parity, the whole coding-agent runtime or any full phase gate.

The [authored input](../../fixtures/pi-v0.99.1/agent/awaited-parallel.input.json) supplies one fake model/stream, tools A/B/C, fixed timestamps and call IDs. The real upstream loop normalizes the transcript, declares tools, validates arguments, runs preflight and after-tool hooks, schedules the batch, emits lifecycle events and returns tool-result messages. The stream uses upstream `AssistantMessageEventStream`; no provider module or registry is replaced. A harness-side `Date.now` override supplies the clock and is restored after capture. No upstream source or export changes.

The [captured expected output](../../fixtures/pi-v0.99.1/agent/awaited-parallel.expected.json) records raw prepared requests, ordered awaited events, provider emission snapshots, tool/hook traces, control gates and final returned messages. Measured checks:

- Waiting at assistant `message_end` leaves tool preflight/execution at zero.
- Preflight runs A/B/C before three tools execute concurrently.
- Explicit gates produce C/A/B completion events; returned tool results retain A/B/C order.
- All three results request termination, so the run makes one provider request and emits one `agent_end`.

Task gates establish order without sleeps. Every event is cloned synchronously at emission before later mutation; provider emissions and agent events are separate. Each child gets a fresh home/workspace and credential-free environment. The existing offline guard prohibits network and child-process builtins. This narrow trusted-source capture is not an OS sandbox.

The [manifest](../../fixtures/pi-v0.99.1/agent/manifest.json) records provenance, clock/IDs, source SHA and input/expected checksums. [full-lock.json](../../tools/PiReferenceRunner/full-lock.json) records 29 loaded upstream files with checkout and canonical Git blob hashes, 670 loaded dependency files, complete installed package tree hashes, Node hash, npm archive/version and dependency projection hashes. A passive loader observer forwards original loader output unchanged. The upstream resolver handles workspace aliases. Source cleanliness and complete dependency trees are checked before and after runs.

The initial golden was captured twice using `--capture-new`, which refuses an existing golden, lock or manifest. A later validation-only edit moved checksums before execution, added read-only `--oracle PATH` and included the existing strict JSON parser in verification hashes. The lock retains original capture hashes and the explicit amendment. Expected output bytes were not rewritten; default verification afterward again ran twice and matched.

Run from the repository root using the locked Node executable:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/PiReferenceRunner/full-run.mjs --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1'
```

The explicit `--oracle` command works from an isolated PiSharp review clone; the external oracle is read-only during qualification. Without this option, the helper uses the implementation checkout's sibling oracle path. Either path must match the source/dependency/runtime pins.

Default mode verifies pins and expected-input checksums before child execution, compares two repeats, then compares raw JSON to the frozen golden. It writes separate actual output and report under ignored `artifacts/full-reference/`. Object key order may differ; arrays, missing/null, exact strings, opaque fields and numeric lexemes remain significant. Unsupported numeric precision and duplicate names fail explicitly.

The main fixture manifest is unchanged; this family has a separate manifest pending integration. All 45 mandatory Deferred rows, production adapters/catalogs, additional agent scenarios, sessions/RPC/extensions, platform coverage and full Phase 1/2/3 gates remain open. No native acceptance is inferred from this upstream-only capture. Frame-reducer qualification is a separate next slice.
