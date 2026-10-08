# MCP resource semantic leaf R296

Base commit `6170379555cc9d817cdf3a360857a319ab8ba31b`, tree `8405c976f4ded93746e6ff88532e26d6a4ce0cfc`. Original read-only oracle commit `d86654abb8862e201933517d6f1fce9f88dd117f`. No applicable AGENTS.md or .agents files were present in this clone or workspace root.

Implemented source is a reusable execution leaf, with no channel acquisition, lease close, registry publication, host registration, process, network, credentials or ambient file writes. Hosts supply a call-time server snapshot and request callbacks already admitted through the existing owning runtime. The callback receives captured server generation separately from native extension owner and session generations in McpRequestOptions.InvocationIdentity. It must validate server generation and capability and join the original channel request, cancellation and retirement work before returning.

`McpResourceTools.ExecuteAsync` handles the three original public names. Selected server listing returns one page and optional cursor. Aggregate listing sorts captured server names ordinally, drains all pages, awaits every started request and reports per-server errors. MCP App resources are filtered; icons and _meta are removed from listings. Read contents retain script-facing contents without _meta, label multiple items by URI, convert text, image and textual MIME blobs, and send binary output only through the injected saver. Empty reads produce the original empty-resource message. Model text is limited to 20 KiB by default, using UTF-8 boundaries and the original middle marker and warning; structured content remains complete within admitted response limits.

Limits bound pages, response bytes, entries and retained listing bytes. A shared aggregate budget charges retained pages, including pages discarded after a server failure, before aggregate append. Concurrent responses are individually bounded; host concurrency and total server snapshot limits remain explicit deployment policy. Non-image base64 uses strict native decoding; malformed base64 is rejected, unlike Node Buffer's permissive decode. Aggregate order uses ordinal server names rather than localeCompare; these native policy differences need corpus review, not implied parity acceptance.

Injected output saves are borrowed originals and are awaited directly; cancellation cannot abandon them. Output callback reentry into this leaf refuses before further requests. Host callback reentry and close are governed by the existing runtime. Ordinary saver failures become original-style model notices. An operation canceled after saver settlement propagates cancellation. There is no detached refresh or retry.

Source mapping:

| Original source | Native source / behavior |
|---|---|
| packages/coding-agent/src/extensions/mcp/resources.ts | McpResourceTools selected page, aggregate list, resource App filtering, metadata removal, read conversion |
| packages/coding-agent/src/extensions/mcp/tools.ts:120-203 | injected binary/text output saver and text/image content conversion |
| packages/coding-agent/src/core/tools/truncate.ts:278-314 | UTF-8 middle truncation marker, counts and boundaries |
| packages/mcp/src/protocol/content.ts:95-149 | text/image model content shape |

Eight synthetic control groups are authored in McpResourceTests.cs. Held request and saver controls prove originals remain pending until synthetic release; owner cancellation is propagated only after join. Separate generation domain and stale-owner rejection controls use supplied synthetic callbacks. Cases are intentionally unregistered and unexecuted. No build, test or original differential was run.

Remaining host work: add resource methods to the existing runtime's owned operation path and resource capability check; retire disconnected channels without replay; expose real call-time connected resource servers; register all three callbacks/schemas through the single prepared registry and existing publication pipeline; assign genuine discovery identity only through that host pipeline; supply an admitted output sink and lifecycle retention; connect list_changed/reload retirement as appropriate. Codemode/tool-search exposure, OAuth/reauth, physical transport evidence and original differential acceptance remain separate holds. This source does not close the whole MCP parity row or qualify the producer.
