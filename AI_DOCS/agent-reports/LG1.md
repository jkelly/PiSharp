# LG1 — distinct legacy Chord protocol 4

Status: implementation and offline captures written; centralized .NET validation requested from I13, results pending. Requested model/effort: gpt-6.1-sol/xhigh; requested tier Ultrafast, effective tier unverified. No commits, publication, network calls, credentials or package installs.

## Boundary and coordination

Read D14, current implementation context/plan, exact applicability rows, and the pinned legacy source at `E:/AI/PiSharp/upstream-v150` (`f9a26365568127461c47a07a37dcb42c140a3ac5`). These are `packages/chord/src/*.ts`, not `next`.

The original LG1 scope contains fifteen substantial rows. Root split independent implementation into LG2 (`Legacy/Contracts.cs`, `Errors.cs`, `Runtime/**`) and LG3 (`Legacy/Cluster/**`). LG1 owns `Legacy/Protocol/**`, `Legacy/Links/**`, and corresponding wire/link/flow/session tests. There is no platform-packaging no-op among these rows; Node-specific operations require explicit native adapters and evidence.

## Implemented

- `LegacyFrame`, `LegacyProtocol`: separate protocol constant 4 and complete open-object frame schema, including description tokens, `shape`, `sub`, `resync`, `again`, `origin`, roots and reload controls. Omitted args/results remain omitted. Unknown future frame fields are admitted; v2 frames are rejected. Structured Chord errors retain code/reason and their prefix rules.
- `LegacyCodec`: 65,536 UTF-16-character threshold, raw DEFLATE with explicit numeric level 1/default strategy, IPC `{ chord2: ... }` plus `z: [field,base64]`, text WebSocket frames and binary `[u32-BE envelope-byte-length][envelope with z][deflated payload]`. `LegacyPacked` relays bytes without inflate/recompress. Native consumers can impose an explicit inflation bound. Untrusted WebSocket binary acceptance is configurable.
- `ILegacyLink`/`LegacyLink`: receive-before-listener backlog, detached FIFO memory links, idempotent close and late close notification; Node IPC channel abstraction with completion/error callbacks and `ipc send failed: EPIPE`; generic WebSocket interface and actual `System.Net.WebSockets` adapter. `LegacyPipeLink` is explicitly a .NET framed-pipe protocol, not Node IPC serialization. Asynchronous native writes and bounded close prevent synchronous pipe/socket writes from holding the runtime indefinitely.
- `LegacyConnection`: negotiated ACK support, cumulative non-ACK frame counting with reported encoded-size accounting, 256-KiB default window, four-window pending-op cap, lazy per-stream reset/resync recovery, FIFO state/unit slots, urgent first units, control/call/result bypass, protocol mismatch and configured-role enforcement, principal/origin stamping and restamping, heartbeat. Void operations post without cross-dispatcher blocking; callbacks never run under the queue mutex.
- `LegacyPeerSession`: actual local-provider and consumer transfer, atomically hydrated shapes, state subscriptions and independent per-stream delta decoder state, calls/void omission/cancellation/errors, incoming-call bounds, late-provider cancellation cleanup, release/close, relays with remapped hold/call IDs, first-unit urgency, overflow resync and uninflated payload forwarding. Only parent links may forward principal/origin/root authority. Root admission/lease/destination/end hooks let the host retain the actual generation while calls cross relays. Revocation closes matching pending/active holds and their work. LG2 binds this to the native legacy host; LG3 supplies route discovery and cluster participation.

Shared foundations are JSON serialization/schema/Delta helpers. The wire delta helper is the exact common `delta/index.ts` algorithm, reused with R1 owner agreement; no v2 frame or host lifecycle is used for a v1 claim.

## Evidence

Two checked-in generators execute the unmodified pinned source, with no install or online access:

```
node tests/PiSharp.Chord.Tests/Legacy/capture-codec.mjs E:/AI/PiSharp/upstream-v150 <fresh-output>
node --conditions=source tests/PiSharp.Chord.Tests/Legacy/capture-wire.mjs E:/AI/PiSharp/upstream-v150 <fresh-output>
```

- `fixtures/codec-v150.json`: 8 observed Node v24.22.0 cases with source SHA256, exact text/IPC/WebSocket bytes, compressed bytes, sizes and inflated text; covers void omission, UTF-16/lone surrogate, threshold boundary, large state and large args. Generation executed successfully.
- `fixtures/wire-v150.json`: 42 observed schema verdicts with source SHA256; every frame family plus malformed IDs/kinds/flags/ops/errors, open fields and v2 rejection. Generation executed successfully.
- Independently re-read both fixtures: 42 wire cases (30 accepted, 12 rejected), 8 codec cases; both recorded source SHA256 values match the pinned files.
- Dedicated native cases compare both captures, and exercise malformed/truncated/bounded packing, FIFO backlog, close, IPC failure, roles, flow-cap/reset/resync, control urgency, unsized links, actual transfer/cancel/errors, atomic hydration, pending providers, call ceilings, multi-hop relay authority and large packed payloads, actual named-pipe and actual .NET WebSocket framing.
- Owned-path `git diff --check` passed. This is a source/whitespace check, not a runtime test result.

## Acceptance limits

Do not infer completion of `contracts`, `contract-schemas`, `objects-declarations`, `hosts-facets-principals`, `references`, `reload`, `cluster-reload`, `kernel-retention`, `host-manager` or `registries-v1` from this transport package. LG2/LG3 own their implementation/evidence and host integration. A supplied Node IPC channel adapter has exact envelope/failure semantics, but does not by itself prove a launched Node child process. Actual native pipe/WebSocket tests are offline platform evidence, not a real browser/Node interoperability run. Final acceptance requires the centralized compile and test receipts and stable integrated runtime tests.

## Observed validation follow-up

I13's `artifacts/gap-validation/Chord-legacy-runtime2-ddf7ba011f3e49c58bcdb0d0d4d58cd5` executed 31 integrated legacy groups with 3 failures. LG1 had two failures; its remaining 14 groups passed, including actual pipe/WebSocket transfer, remote call/state/cancellation, relays and the 42 captured frame verdicts. This is not a passing full-suite receipt.

- The close test polled `Closed` on a second thread, which can observe the close flag before the callback executes. It now awaits `OnClose` itself and then verifies exact bye reason, closed status and exactly one callback after repeated closes. The production exactly-once guard is unchanged.
- The captured lone-surrogate frame exposed `Js.FromNode` calling `JsonElement.GetString`, which rejects escaped lone UTF-16 surrogates. I01 fixed that boundary with `JsonUtf16.GetString` and added public schema regressions. Independently, `LegacyCodec` now parses leaves into owned `JsonElement` values, so the runtime can inspect the retained code units instead of receiving write-only RawJson values. The golden test now also validates every decoded frame and requires exact text replay. No fixture was removed or altered.
- `InflateMessage` now splices raw inflated JSON text as upstream does; a regression preserves payload whitespace, duplicate keys and number spelling instead of parsing/reserializing a text relay's payload.

The final rerun after these fixes is pending I13. LG2 separately owns the native-function validation failure.

### Compression backend finding and acceptance boundary

Root's stable full Chord run `root-chord-final-r2-d2bb9fedf24f4984aa36fd99f8418c9a` passed 866/867 groups. The remaining failure was the test assuming that recompressing the same JSON creates identical DEFLATE bytes.

Exact upstream `link.ts:154` uses `deflateRawSync(json, { level: 1 })`; it does not select a canonical encoder. The checked Node v24.22.0 reports zlib `1.3.2.1-motley-285e94b`. The exact .NET 10.0.12 `System.IO.Compression.Native.dll` contains backend identity strings `zlib-ng 2.2.5` and `1.3.1.zlib-ng`. The observed source stream uses a dynamic-Huffman block while the native stream chooses a fixed-Huffman block. Both raw DEFLATE encodings carry the same payload. The implementation now names numeric level 1 and default strategy explicitly, rather than relying on the `Fastest` alias.

The original fixture bytes and sizes are retained. Acceptance remains byte-exact for uncompressed text, binary length-prefix/envelope bytes, IPC envelope/field and packing threshold. Captured compressed WebSocket and IPC bytes must decode to the exact original payload with a 2-MiB inflate cap, and relaying each captured message must reproduce its compressed bytes unchanged. Native encoded sizes must report actual transmitted bytes. Regenerated compressed payloads are checked for semantic interoperability rather than noncanonical byte identity: a bounded independent Node `inflateRawSync` subprocess decodes every native WebSocket and IPC fixture, checks exact inflated text/data and fails on timeout or missing Node. There is no skip path. It uses `node` on PATH or the explicit `PISHARP_NODE_EXECUTABLE` override, no package imports or network access. The verifier self-check passed all eight original source fixtures, including all three packed cases. Independently extracted the 678-byte native message from the failed receipt: Node inflated it under the 2-MiB cap to exactly the 65,537-character payload of the 340-byte original captured message. The full native rerun remains with root/I13.

`Chord-full-stable5-1de719f187e34765bf2673ec3c99e37e` built and executed all 867 groups (866 passed). Its sole failure was the new test subprocess inheriting Windows redirected-stdio encodings: expected Unicode sent to Node was corrupted, and Node's UTF-8 error text was decoded as mojibake. The verifier harness now sets stdin/stdout/stderr to explicit strict UTF-8 without a BOM. Exact Unicode comparisons remain intact. Large-string comparisons still compare the actual strings/data exactly, but failures print only UTF-16/UTF-8 lengths and SHA256 instead of huge payloads; unexpected stderr is capped at 4,096 characters. Node verifier self-check passed all eight source fixtures; an intentionally changed Unicode expectation failed with an 874-character hash/length diagnostic. No product payload or fixture bytes changed for this harness correction.

LG2/LG3 subsequently traced their 24/25 cluster result's existing-reference post-reload hang to the retained provider `StateChanged` listener synchronously invoking the connection dispatcher. A received `commit` was waiting on host publication while that publication waited on the same connection. The listener now uses the existing FIFO `Connection.Post`; error reporting remains owned by that connection. The actual LG3 post-reload case supplies the regression, and its rerun remains centralized with I13. Other runtime code was left unchanged in coordination with LG2/LG3.
