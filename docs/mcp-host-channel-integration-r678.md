This source successor composes the previously frozen registration facade and
joined dynamic-roots/incoming-request fixtures over the accepted a9 producer.
It changes only the allocated shared JSON-RPC channel and adds a concrete CLI
registration bridge, two fixture files, and this document.

The actual channel now owns its generic incoming registry over its already
admitted wire.SendAsync. Custom handlers use method replacement/identity-bound
removal. Incoming IDs and notifications/cancelled enter that owner; physical
result/error sends are directly joined. Dynamic roots installs its actual handler
before channel start and adds roots capability to the existing initialize request,
preserving unrelated capabilities. Channel close starts registry/root stop before
wire retirement and joins all initiated originals and the existing callback slots.
Default/static roots dispatch and default close fault identity remain unchanged
until a handler or dynamic-roots provider explicitly opts into the new owner.

The CLI bridge is a concrete metadata publisher, not a supplied synthetic
publication receipt callback. BindOwner validates a genuine already-active native
RegistrationScope against its exact ExtensionRegistry using an uncommitted empty
tool-catalog preview. This check has no registry publication effect. Registration
then atomically updates the bridge's composed configured/extension metadata
snapshot; configured entries keep precedence. CreateRuntimeFactory passes that
exact committed snapshot to one explicitly admitted host acquirer and delegates
activation to the existing McpSessionRuntimeFactory -> McpAdmittedActivationHost
-> McpPreOpenServerCapture pipeline. Substituted catalog admissions join discovery
and native resource originals before rejection. Metadata retires only after the
provided stable actual scope-disposal original is independently captured and
successfully joined.

Eight new controls are authored and unexecuted: five exercise actual channel
dispatch/capability/reply/cancellation/close paths on an injected in-memory wire;
three exercise real active registry scopes, configured precedence, original scope
retirement, rejected acquisition cleanup, and genuine pre-open initialize/tools
discovery followed by prepared registry/tool binding. The tests use fake channels,
denying policies and an unavailable provider; no process or live HTTP is acquired.
Held source and physical-send releases and channel/runtime original joins remain
in finally paths. Root owns Cases/prefix registration and runtime qualification.

Required shared host patch remains explicit: create this bridge for the actual
extension registry and loaded configuration; expose each bound facade after
ActivateAsync; use bridge.CreateRuntimeAcquisition inside the existing profile
admission while preserving its captured native registry, exact final policy and
runtime-view binder (or CreateRuntimeFactory for a standalone admitted host);
build McpSessionRuntimeAdmission using its exact supplied catalog and explicitly
admitted server factories; configure dynamic roots/custom channel handlers in
that admitted channel factory before Start; call RetireClosedOwnerAsync after
the actual native scope disposal. Existing OfflineSessionProfile.Mcp.cs factory
configuration is not changed by this source. Host owner activation/retirement
and replacement must be serialized. This slice does not implement initializer
staging or publication into an already-attached live session. Those broader host
transaction changes remain coordinator-owned requirements, together with MCP
OAuth. No full original SDK/MCP parity, live credentials or runtime pass is claimed.
