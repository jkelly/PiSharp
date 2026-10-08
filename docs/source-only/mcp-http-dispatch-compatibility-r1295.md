# Bounded HTTP dispatch compatibility

The pinned v0.99.1 `streamable-http.ts` lowercases content types before dispatch (155–156, 443–450) and rejects unsupported POST response types before acquiring a reader (239–250). This change applies the same case normalization to both native GET paths and places the existing unsupported-media refusal before native body acquisition.

Request-specific physical Stop, response/body disposal, full original fault retention, cancellation fencing, finite reconnect caps, SSE resumption and no POST replay remain unchanged. Native cleanup faults continue to propagate rather than matching the original's swallowed response cancellation faults.

Two separately owned, unexecuted control groups use the actual `McpStreamableHttpTransport` and supplied request operations only. They cover lowercase/mixed-case GET and resumed GET, unsupported POST with a forbidden body-acquisition effect, independently held physical disposal and request stop, exact cleanup siblings, and exact request-send sibling faults. Source originals and cached full aggregate/direct faults are exported; bounded fault checks reject unknown branches. No shared fixture or Program registration changes are included.

Malformed-frame recovery and unknown-response diagnostics still require a separately admitted nonfatal diagnostic seam. Healthy retry reset, status-body/challenge diagnostics, timer/JSON bounds, POST trailing data, generic callback/sampling/elicitation integration and ambient acquisition are outside this patch. No native/compiler/Node/test/network execution or inherited runtime credit is claimed.
