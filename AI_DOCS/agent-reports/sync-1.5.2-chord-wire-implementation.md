# C-W: Chord 1.5.2 wire and remote implementation

Implemented against frozen upstream `b4afdddf44e059c67ccf5097e5317d913521b470` in `E:/AI/PiSharp/upstream-v152`. Legacy protocol APIs and the existing 1.5.0 wire fixture remain intact. Requested ultrafast execution tier cannot be independently verified.

## Shared contracts

- C-K supplies `OldWork.Of(ChordContext)`, `OldWork.AsRemote(ChordContext)`, `ITarget.StateFor(version,fingerprint)`, `ChordObject.OldServes`, the exact attempt through `FollowingAttempt` / `Instance`, and `ChordHost.WithTelemetryParent` / `Telemetry` / `PrincipalAttributes` / `LocalObject(..., context)`.
- K deliberately uses the retained context chain without the upstream old-count fast path. A remote old marker therefore survives cancellation of the public waiter while the original callback retains its context. C-W does not incorrectly release a provenance lease when only the public wait ends.
- T1 supplies `IReadOnlyDictionary<string,string>?` carriers, `OpenSpan`, `ChordTelemetry` and cached metrics. `IUpstream.Span` exposes a pending hold's span for K's route-resolution linkage.
- Frozen text positions count payload fields excluding the kind tag: hold trace field 10 (array index 11), call trace field 5 (array index 6), call old field 6 (array index 7). Calls with trace or old use explicit null argument and segment placeholders. These positions were checked against executed upstream codecs, rather than inferred from the discovery prose.

## Behavior

- Protocol 1 retains old no-extension bytes. Binary flags are old `0x20`, trace `0x40`, origin `0x80`. Trace is serialized after optional origin and before ordinary message fields. Only hold/call may carry trace; only call may carry old.
- Carrier keys are nonempty printable ASCII without spaces, maximum 256 UTF-16 units; values allow printable ASCII including spaces and empty strings, maximum 256; maximum eight properties. Hold type/address-kind bounds are 256. Native byte arrays and `ReadOnlyMemory<byte>` retain explicit nested payload extraction.
- Trusted calls carry remote old provenance; relays strip untrusted old flags even with telemetry disabled. Method carriers become provider method parents; call, hold, relay, re-hold, link-byte, link/hold-count and backpressure instrumentation use T1.
- Loading retains already-sent consumer calls and provider calls. It rejects not-yet-sent consumer calls because their numeric method indexes belong to the old binding. Failed/gone/release terminate their relevant calls. Providers retain the prior variant/method-index contract while pending, and until the consumer acknowledges the fresh own Bound message. Own sent/acknowledged counters are independent of relayed ACK attribution.
- A successful or failing local/relayed answer flushes all pending local state in one counted own message before the answer, including past the K=2 window. Empty pending state adds no frame.
- Heartbeats compensate for scheduler pauses detected by the host's shared timer, ping immediately after waking, and retain the close-minus-ping response window. Actual silence still closes links.
- Native WebSocket close writes are serialized with data sends. Oversized messages send 1009 and allow the peer to complete the close handshake; transport abort remains a bounded fallback. A logical failed link closes immediately while the socket finishes its close exchange.
- Revocation uses canonical own-principal equality independently of a custom principal key, and emits the synchronous revoke count span.

## Evidence and tests

`tests/PiSharp.Chord.Tests/Link/generate-goldens152.mjs` executed successfully under Node 24.22.0 and captured **53 frames** from the frozen codecs into `tests/fixtures/chord-link-v152.json`. TypeScript erasure leaves the binary/text implementations unchanged; unrelated schema/telemetry imports are substituted with fixed shims. No C# expected bytes were used to create this fixture.

New reflection-discovered groups: `Link152Cases` and `Remote152Cases` (prefixes `link.152`, `remote.152`). They cover frozen codec roundtrips and cross-codec bytes, exact flag/placeholder positions, malformed carriers, pause/real-silence timing, nested byte payloads, real loopback WebSocket 1009 handshake, loading with queued-call rejection and running-call retention, a full call window followed by reordered Bound/ACK without stale-index transmission, failure cancellation, trusted incoming/relayed old flags, state-before-result with a full window, method reorder before/after own Bound ACK, trace propagation through a relay, and mixed local/relayed state-before-error with ordered ACK attribution.

Updated three obsolete existing expectations in `RemoteCases.cs`: full-window replies now include their pending state, old callback release precedes awaiting reload completion, and a successor's attachment starts after the old callback drains. The tests continue asserting old-result preservation and exact final state.

No dotnet build or C# test was run by C-W; root/validation owns serialized builds and will append their evidence. `git diff --check` passed for C-W files.

Review correction: read-only reviewer `mcp_transport` caught an overbroad interpretation of loading retention. Frozen `remote/consumer.ts` explicitly rejects calls still queued at the consumer's call window; only sent calls remain in flight. C-W verified that source, restored the queue rejection, and added a deterministic reordered-variant regression. Initial serialized validation also found required `HostOptions.OnError` missing in three new test hosts; those initializers were fixed. Subsequent build/test evidence remains owned by root/validation.

Full-r3 runtime follow-up: the new trace fixture incorrectly connected the relaying host as a child of the final provider; a child link correctly rejects origin-tagged messages. It now uses `Route` resolving to the provider's `Connect` parent link, following the established route fixture. The parked-restart test also incorrectly expected `ReloadAsync` to await an attachment becoming live; frozen `host.ts` explicitly reports readiness without waiting for live. It now checks the `restarted` report while attachment is blocked and then observes `Bound` after release. These were fixture corrections; protocol trust and reload behavior were preserved.
