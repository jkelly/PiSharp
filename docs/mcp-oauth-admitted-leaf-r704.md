This source-only leaf implements the state portion of the pinned Pi v0.99.1
`packages/mcp/src/oauth/provider.ts`, commit
`d86654abb8862e201933517d6f1fce9f88dd117f`.
The inspected archived checkout file has SHA256
`fc0ae0e6d54ef0ddcba715f3555b570ddebd93d914dd3f8489ae08304151395d`.
Its baseline provenance comes from `compatibility/baseline.lock.json`; the upstream
archive directory has no Git metadata, so no local upstream HEAD verification is
claimed. Source link:
https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/mcp/src/oauth/provider.ts

`McpAdmittedOAuthStateProvider` implements `load/own/update`, `saveTokens`,
`saveCodeVerifier/codeVerifier`, persisted client/discovery metadata, and selective
`invalidateCredentials`. Foreign stored server URLs produce empty local state and
are replaced only by an explicitly requested save. Token expiry is computed at
save invocation from an admitted millisecond clock; tokens without expires_in
clear earlier expiry metadata. Reads fence writes admitted before that read.
Updates serialize against fresh stored state and preserve unrelated modeled fields. A
failed write poisons subsequent writes and reads as in the original promise chain.
A standalone read failure does not poison the write lane.

The caller supplies an absolute normalized Uri, an explicitly admitted store and
clock. No default durable or memory store, secret lookup, filesystem access,
environment access, HTTP exchange, browser redirect, callback listener, entropy
source or credential acquisition is installed. Source authoring uses synthetic
strings only. Store operations are awaited without detached work or cancellation
abandonment. Each ValueTask is captured once; callback failure retains its exact
original Task and complete unflattened fault evidence in McpOAuthStoreException.
The caller owns store lifetime and admission. Store callbacks must settle and must
not await this provider's own queued writes/reads (that would self-depend). Hosts
must separately serialize external store writers; this lane covers this instance.

State records and JsonData metadata are immutable; caller-supplied token and state
values do not appear in their generated ToString output. This does not replace a
host's secret logging/storage policy. URL comparison is ordinal after .NET Uri
normalization. WHATWG URL/.NET Uri cross-platform normalization equivalence remains
unqualified; the authored controls use ordinary HTTPS URLs and deliberately retain
path/trailing-slash distinction. Configured client precedence, client metadata
defaults, random OAuth state, redirect delegation, discovery HTTP, registration,
PKCE generation, authorization-code exchange, refresh and transport integration
remain required. This is not whole OAuth or whole MCP parity.

Six authored, unexecuted synthetic controls are exposed through
McpAdmittedOAuthStateProviderTests.Cases() with prefix `mcp-oauth-state.`. They cover
exact-server isolation, expiry reset, every selective invalidation, a held physical
store save/read fence and ordered updates, duplicate nested failure evidence and
sticky-write suppression, missing verifier and synchronous read-failure recovery.
The isolated fixture successor releases held work in finally, directly joins every
captured request and physical store original, and retains the primary failure plus
each full unflattened original fault inventory. Synthetic two-original failure
controls retain repeated/shared causes and the primary assertion. Faulted clear-token
OCE and canceled store originals retain their exact original Task/status while the
provider wrapper remains Faulted; subsequent writes/reads retain sticky failure.
No production provider change is included. Root owns Program registration,
source review, serial compiler/runtime qualification and host installation. Shared
project files, Program files, transport and host call sites are unchanged.
