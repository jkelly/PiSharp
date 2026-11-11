# C-HTTP implementation

Implemented the reviewed D-CHO/D-TEL HTTP contracts in `src/PiSharp.Chord.Hosting/Http/` with new cases in `tests/PiSharp.Chord.Hosting.Tests/Http152Cases.cs`. No build or test process was started: root owns serialized verification. Requested ultrafast execution tier was not independently verified.

## Public seams

- `HttpRoute(HttpHandler Handler, bool Page = false, bool CrossSite = false, bool Public = false)` preserves earlier positional arguments. CrossSite implies anonymous access; ordinary routes require a principal.
- Legacy `HttpContribution.Sockets : IReadOnlyDictionary<string,HttpSocket>` remains source-compatible and private. Additive `SocketOptions : IReadOnlyDictionary<string,HttpSocketOptions>` uses `HttpSocketOptions(HttpSocket Handler, bool Public = false, int? MaxPayload = null)`. Duplicate patterns across either dictionary are reported. Contributions have `ScriptPaths : IReadOnlyList<string>`.
- `HttpRequestContext.RemoteAddress : IPAddress?` reads only the actual connection peer, including on verified relay requests.
- `SignedCookies.ReadSigned(...) : SignedCookies.SignedValue?` returns `(string Value, DateTimeOffset? Expires)` with the existing signed wire format and expiry checks. `Read(...)` delegates to it.
- `ChordHttpOptions.LinksOpened : Func<HttpRequestContext,JsonObject,Task<Func<Task>?>>?` runs after WebSocket acceptance for links. Its returned asynchronous cleanup is called once in socket finalization. Hook failures report `hook_failed` with identity `links.opened`; link-options hook reports `links.options`.
- `ChordHttpOptions.Telemetry : ITelemetryContext?` uses T1 `ChordTelemetry` and `OpenSpan` helpers. The C-K owner supplied `ChordHost.IsLoaded`.

## Admission and lifecycle

Unloaded hosts return 503 before registry acquisition. Authentication precedes private route handlers and wrappers. Missing principal returns 401 for private routes, legacy sockets, private option sockets and links. Public sockets may run anonymously; their admission is withdrawn if a later router generation retains the same delegate but makes it private. Existing callback dependency leases and request handling remain in place.

Script paths are combined across contributions, validated as single-origin absolute paths, and expand against the admitted request origin in `script-src`, alongside the nonce. The original self-origin fallback remains when no scripts were contributed. The same defaults are applied on error responses.

Custom-socket message size enforcement now sends `MessageTooBig` (1009), waits up to five seconds for the close handshake, and only aborts an unresponsive peer. The outer socket handler avoids unconditional aborts while closing. The wire owner confirmed its separate WebSocket port now uses serialized close output, 1009/1003 protocol closes, peer-close reply and a two-second handshake fallback in `Link/Transports.cs`.

HTTP telemetry records only matched method/pattern and host, through body delivery. Direct request tracing headers are ignored. Only a contributed relay's verified admission can supply a single printable-ASCII traceparent (1–256 chars), and optional tracestate (1–512 chars). Oversize, multivalue and control-bearing headers are omitted. Final HTTP status is recorded; server failure marks an error, while request cancellation preserves OK status.

## Verification prepared

Root registration requested:

- `http.152-admission-headers-cookies` → `Http152Cases.AdmissionAndHeaders`: immediate unloaded503; no private handler/wrapper execution before401; public and CrossSite success; actual peer despite spoofed forwarded header; admitted-origin path CSP including failure response; authenticated private success; signed/session/expired cookie cases.
- `http.152-trace-trust` → `Http152Cases.TraceTrust`: direct trace ignored; verified-relay carrier accepted; oversize trace ignored; settled pattern-only HTTP spans.
- `http.152-sockets-hooks` → `Http152Cases.SocketsAndHooks`: real listener private socket/links401, no denied callback/hook, public socket acceptance with oversize1009 and acknowledgement, authenticated link-open and cleanup exactly once.

`git diff --check` on the owned HTTP and new test paths passed. Runtime results remain pending root's serialized build/test gate. Root was notified to mark intentionally anonymous legacy fixtures public (leased plugin route, response/file routes, slow-drain route) and register the new cases. No unrelated source, shared test runner, or package metadata was edited.

Validation's first hosting build identified CS8601 at `ChordHttp.cs` default LinkOptions.Principal assignment. Corrected to the already-guarded non-null principal; no local build run.

The validation owner subsequently authorized edits to legacy HTTP methods in shared `Program.cs`: marked `/files` and `/page` public where foreign anonymous access is explicitly expected; marked anonymous `/bytes`, `/file`, `/escape` and `/slow` fixtures public; initialized the synthetic connection peer address. This fixes fixture assumptions after deny-by-default rather than weakening production admission. With validation's subsequent authorization and plugin-owner coordination, the generated `/leased` fixture also gained `Public:true`. Shared registration was not edited.
