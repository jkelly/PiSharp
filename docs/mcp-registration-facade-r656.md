The additive owner-bound MCP facade projects the pinned Pi 0.99.1 extension
`registerMcpServer`, `unregisterMcpServer`, and `getMcpServers` surface. Its
`McpRegistrationCatalog` keeps registration order, validates and copies inert
configuration through the existing reader, refuses replacement by a different
captured owner, and makes absent/foreign unregister a no-op. Snapshot rows are
copies. Global/project configuration retains precedence through the existing
`McpCatalogPlanner.ComposeServers` method.

The catalog requires an actual `IExtensionRegistry` scope, an explicitly admitted
absolute extension path, one host active-owner assertion, and one synchronous
admitted publication dependency. The assertion must validate the owning registry's
actual active/initializing state; cancellation alone does not establish that state.
The publisher receives the exact owner, revision, before/after registrations and
composed catalog. It must use the existing host registration/prepared-publication
pipeline and return the exact owner/revision receipt only after atomic commitment.
The owning host calls `RetireOwner` during scope retirement to remove only that
exact scope's registrations, even after its lifetime token is canceled.

This source slice cannot install a host adapter because those shared host and
registry files are reserved to the root coordinator. It supplies a concrete
integration dependency and metadata implementation; it does not claim that the
existing CLI/plugin entry point already exposes the facade. Root integration must
bind it to initialization staging, successful activation, live changes and owner
retirement. If publication throws or returns an invalid receipt, the catalog
fences subsequent work without replaying effects whose commitment is uncertain.
Reentry from either the owner assertion or publisher rejects before mutation.

No configuration value acquires a process, HTTP client, environment expansion,
credential, callback listener, filesystem permission, or transport. OAuth flow and
dynamic roots/custom server-request handlers remain separate missing features.

Nine authored controls use genuine `ExtensionRegistry` activation scopes with
injected metadata publishers. They cover ordered same-owner replacement and
copied snapshots; foreign ownership/no-op withdrawal; configuration precedence;
validation with inert unknown metadata; inactive/canceled admission; publication
reentry; original publisher-fault identity and replay fencing; foreign receipt
identity/revision; and exact-owner cleanup after close. They are source only,
unregistered and unexecuted. The root must add `McpRegistrationFacadeTests.Cases()`
once to the reserved Program and include its prefix in the direct-original await
branch. No native/build/compiler/test/Node operation was run by this source author.
