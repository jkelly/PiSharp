# Injected HTTP-to-SSE transport

This is a bounded native P2-04 primitive. `HttpSseTransport` sends a caller-created `HttpRequestMessage` through an injected `HttpClient` and yields the accepted decoder's `SseEvent` records. It has no provider adapter, normalized assistant events, authentication, endpoint/proxy policy, WebSocket mode or reconnect behavior. Full P2 and provider parity remain open.

## Borrowing and request behavior

The caller owns the client, its handler chain, the request and request content. The transport never disposes those objects, clones the request or adds headers. Method, URI, version/policy, headers, content and request options reach the injected handler through normal `HttpClient` behavior. Client default headers and handler behavior still apply.

Construction validates all `SseDecoderOptions` before any send. `SendAsync` rejects a null request immediately; enumeration starts the send. Each enumeration creates its own decoder and makes one `HttpClient.SendAsync` call with `ResponseHeadersRead`. The transport performs no retry. Redirects, authentication challenges or retries configured inside the borrowed handler chain remain that chain's behavior. A sent request cannot be reused: standard `HttpClient` rejects a second send of the same request. Use a new request for another operation. [H1]

Headers-only completion avoids the client's default whole-content buffering. A custom `HttpContent` can still buffer internally during `ReadAsStreamAsync`; streaming content must provide a streaming implementation. The decoder bounds its line and event state according to [the SSE contract](transports.md); this primitive does not impose a total response-byte limit. [H2, H3]

## Cancellation, rejection and faults

The effective caller/enumerator cancellation token is checked before sending and after headers arrive, passed to `SendAsync` and `ReadAsStreamAsync`, then passed through decoder reads and buffered processing. Already-canceled calls make no send. Cancellation during send, body acquisition or read propagates as cancellation. The transport adds no timeout: with `ResponseHeadersRead`, `HttpClient.Timeout` covers the send through headers, so callers must provide a cancellation deadline for the body when required. [H1, H2, H3]

Only successful HTTP status codes (200-299) enter body decoding. Other statuses throw `HttpSseRejectedException`, a `HttpRequestException` subtype with the numeric `StatusCode` and a fixed status-only message. The transport does not acquire, read, parse or log an error body, reason phrase, headers or request URI. It does not interpret `Retry-After`. This rejection policy is native hardening, not established upstream parity.

Send/acquisition/I/O and decoder failures propagate. Their exception text originates in the injected implementation; this primitive makes no blanket sanitization guarantee for arbitrary handler or stream exceptions. It creates no assistant terminal event and does not settle `ChatRun` results.

## Response and body cleanup

Once `SendAsync` returns a response, the transport owns that response. Once `ReadAsStreamAsync` returns a body, the transport also owns the body. It invokes the decoder with `leaveOpen: true`, then awaits the body's `DisposeAsync` before calling synchronous `HttpResponseMessage.Dispose`. This ordering applies on EOF, early enumerator disposal, cancellation, read/framing failure and a consumer breaking `await foreach`.

Response disposal happens in a nested `finally` even if body cleanup throws. `HttpResponseMessage` disposes its content synchronously; `HttpContent` can synchronously dispose the cached stream again. Injected streams must tolerate repeated disposal. For a rejected status or failed body acquisition there is no acquired body to clean up asynchronously; the response and its content are still disposed synchronously. [H4]

Cleanup is awaited without passing the already-canceled operation token and has no added timeout. A body that never finishes disposal will keep completion/disposal pending. Consumers must drain or dispose the enumerator and use ordinary asynchronous iterator sequencing; concurrent `MoveNextAsync`/`DisposeAsync` is outside this primitive's contract. An enumerable never started has acquired no resources.

Cleanup exceptions follow normal `finally` semantics: a body cleanup failure can replace an earlier operation error; response/content disposal can replace either. All cleanup stages are attempted, but this low-level primitive does not aggregate failures or provide the higher-level `ChatRun` terminal-wins policy.

## Offline evidence and limits

Run `./tests/PiSharp.Transport.Tests/test.ps1`. Its framework-only .NET 10 harness uses injected fake handlers and stream/content gates with `https://synthetic.invalid` requests. It performs no HTTP, DNS, socket, server, credential or provider operation. Tests cover request projection/borrowed lifetimes, UTF-8 fragmentation without content buffering, validation before send, cancellation at each boundary, success/early-break/cancel/fault cleanup ordering, numeric-only rejection without error-body access, read/framing/cleanup/send faults and no transport retry. Gate tests use no sleeps; the harness has only a deadlock safety timeout.

The report marks upstream parity `not-assessed`. These assertions prove native injected lifecycle behavior on the executed Windows .NET 10 environment. They do not prove real-handler pooling, redirect/proxy/auth behavior, other operating systems, provider wire translation or full P2-04 completion.

## Primary sources

- **H1** [.NET 10 HttpClient.SendAsync overloads, cancellation and sent-request restriction](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-10.0)
- **H2** [HttpCompletionOption and headers-only timeout/buffering scope](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)
- **H3** [HttpContent.ReadAsStreamAsync cancellation and custom-content buffering](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcontent.readasstreamasync?view=net-10.0)
- **H4** [HttpResponseMessage synchronous disposal](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpresponsemessage.dispose?view=net-10.0)
