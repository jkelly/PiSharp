# Standalone session HTML renderer

`PiSharp.CodingAgent.Export.SessionHtmlRenderer` is a pure renderer for an immutable
`SessionHtmlSnapshot(Header, Entries, LeafId, SystemPrompt, Tools)`. `Render`
returns the document string and identical UTF-8 bytes without a BOM. It owns no
file, writer, live session, provider, extension callback or theme discovery.
Its caller owns snapshot capture, authorized output, cancellation and cleanup.

The isolated base is `88ecdb9e8a7ced5454317edd9bce9894db364415`, loaded from supplied
bundles. Its tree `31074f90390ac126017e0d398a4c08c577add13d` equals the supplied
canonical `2405f51e2fd2fe3889b871c2c13e8e0dfae8d7db` tree. No canonical worktree trust
or ownership setting was changed.

## Pinned behavior

The original baseline is Pi v0.99.1
`d86654abb8862e201933517d6f1fce9f88dd117f`.
[exportSessionToHtml and generateHtml](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/export-html/index.ts)
capture the header, every physical entry, selected leaf and optional system prompt
and tool definitions. Original output is standalone, with template/vendor scripts,
resolved theme variables, base64 JSON data and optional extension-rendered HTML.
[The pinned viewer](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/export-html/template.js)
uses `getPath` to display the selected ancestry, including stored compactions,
rather than replacing history with effective model context. It also retains
other branches and hidden custom messages.

This native profile keeps those data and ancestry contracts. The selected branch
appears first, followed by other physical entries; a branch index links generated
DOM IDs and records parent IDs, global labels and selected-leaf metadata. Null
`LeafId` explicitly selects no branch. The last physical entry is displayed
separately and never silently replaces null selection. Every entry plus the header
is retained in an HTML-encoded, compact-record JSONL archive. Opaque JSON numeric
tokens and unknown records remain retained, with no format migration.

Text, thinking, tool-call names/IDs/arguments, tool-result names/IDs/errors/content,
recorded bash commands/output, custom messages, compaction boundaries/summaries and
branch summaries have static presentations. Tool results are independent entries,
not paired globally to a same-ID call on another branch. All unknown messages and
unsupported content remain readable as escaped raw JSON or in the archive.

The fixed background fallback follows pinned `index.ts`: `#343541` with brightness
factors `.7` and `.85`, and RGB offsets `+20/+15/+0` for the information background.
This is the original fallback profile, not a claim to resolve the current terminal,
system, dark or light named theme. Foreground and layout styling are native.

## Content safety and limits

All session-derived values are HTML-encoded with framework `WebUtility.HtmlEncode`.
Carriage returns use character references and text blocks nest `code` inside `pre`
so browser parsing does not discard an initial newline.
DOM IDs and link targets are generated from physical positions, never session IDs.
Only generated same-document anchor links are active. There is no script, embedded
script-data block, remote asset, active transcript URL, image source, extension HTML
callback, network request or download behavior. CSP declares `default-src 'none'`,
allows the fixed inline stylesheet, and disables base URLs and form actions.
Images are annotated with encoded MIME type; their original data stays in the
inert archive. Markdown and terminal escape sequences are displayed as text.

The existing immutable forest validator admits the complete captured tree before
rendering. Duplicates, missing parents, cycles, invalid header/entries and absent
selected leaf fail explicitly. Upstream tolerant orphan recovery is outside this
native profile. Metadata diagnostics retain the existing native policy. Prompt
text must be scalar Unicode; optional tool definitions must be an array.

Configured entry, aggregate input-character and UTF-8 output-byte limits are
inclusive. The input subtotal charges header, optional prompt/tools and selected
leaf string before any branch lookup or entry scan. Admission rejects an excessive
metadata subtotal immediately and rejects cumulative overflow after each record,
before inspecting later records. The renderer checks caller cancellation during admission and rendering
and before returning. Failure yields no document result; it causes no I/O to roll
back. It has no authority to cancel a session process or dispose caller resources.

The original interactive Markdown, syntax highlighting, inline images, dynamic
branch selection/search, sharing/download UI, full theme resolution and custom TUI
HTML rendering are not implemented. This is not original-template visual/byte
parity, original-Pi runtime acceptance, full-port completion, Extras or v1.0.2 work.

## Focused test allocation

Seven authored cases are in `SessionHtmlRendererTests.Cases()`, prefix
`session HTML export `. They cover selected ancestry/full archive, tools and hidden
content, hostile markup and Unicode, explicit null/empty selection, forest admission,
exact limits/cancellation, and selector/admission-order bounds. Expectations and provenance are recorded separately
in `compatibility/export-html-text-tool-branch-cases.json`; they are not captured
upstream results or regenerated goldens.

Native build/tests have not been executed in this lane. The test integrator owns
`tests/PiSharp.CodingAgent.Tests/Program.cs`; it must register
`.Concat(SessionHtmlRendererTests.Cases())` in the existing cases enumeration.
After that registration and an authorized build, the existing CodingAgent runner
can use `--dotnet-host <authorized-host> --cli <built-cli-path>` with
`--filter "session HTML export " --report <owned-report-path>`.
Do not credit these seven cases before the existing runner executes them.

RPC codec/dispatcher, session writer and snapshot-capture integration remain
separately owned. This change neither activates `export_html` nor chooses an output
path. Shared implementation status and runtime acceptance ledgers remain with the
integrator; this contract records this slice's authored, unexecuted status.
