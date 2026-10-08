# Admitted MCP activation and executable discovery identity

Source only. Eight synthetic groups are authored; none is compiled or executed.

`McpAdmittedActivationHost.AcquireAsync(catalog, admissions, baseRegistry, autoEnableCodemode,
exactPolicy, prepareDiscovery, token)` composes actual fresh pre-open captures. Every enabled
configuration row needs an explicit uniquely named admission with one config/policy validator
and one capture factory. Every enabled row validates before any factory runs. Disabled rows do
not acquire channels. Config and registered metadata confer no process, HTTP, environment,
credential, permission or semantic-execution authority.

Each factory receives the evolving actual registry and returns `McpPreOpenServerCapture` after
initialize/tools-list discovery. The host checks the exact validated entry and policy, prohibits
capture reuse and mixed reserved generations, and derives requirements from actual discovered
catalogs including exposure overrides. Factories own failed acquisition cleanup. A later failure
joins every already transferred capture independently, retaining primary and cleanup originals.

Final `McpDiscoveryCatalogPreparation` returns `McpPreparedDiscoveryCatalog(Registry, Identities)`.
This is a real rebuilt prepared catalog, not a validation assertion. It must retain all prior
declaration objects, actual leaf adapters, exposure/namespace/default selection and final policy.
Only the registry's private naming wrappers are unwrapped for adapter identity. The host validates
opaque discovery identities against that final catalog; absent required implementations fail.
The host exposes `CatalogPlan.AutoEnableCodemode` as metadata. It does not bypass the owner's
allowed selection or perform a durable active-selection mutation itself.

Pinned Pi v0.99.1 `d86654abb8862e201933517d6f1fce9f88dd117f` identifies codemode/tool_search by
exact name and canonical parameter schema object reference. `McpDiscoveryToolIdentity` preserves
that metadata rule and supplies model-only descriptor factories. Parsed equal schema text and
same-name ordinary definitions fail the reference match. These public metadata factories grant
no genuine executable identity on their own.

Executable admission uses dedicated sealed `McpDiscoveryExecutableDefinition.CreateCodemode`
and `CreateToolSearch`, supplied with typed caller-admitted semantic implementations. Their
private callbacks accept the actual native invocation context, retain normal schema validation
and final-action authorization, and cannot execute before actual owner binding. There is no
JavaScript engine, BM25 ranker, durable search activation or alternate tool execution pipeline
implemented by this leaf. The caller must actually supply those semantics; no unavailable engine
is inferred from a name/schema. Code implementations must route nested calls through the supplied
invocation broker; search implementations must join the owner's durable selection publication.

`McpAdmittedDiscoveryRegistration.Register(registry, scope, definitions)` atomically registers
one or both semantic definitions through the existing prepared batch plan. Use a fresh dedicated
discovery scope/registry separate from each server's dedicated registry. The opaque receipts
capture the exact published snapshot and scope registration; later metadata drift or ID replacement
invalidates them. Definitions are affine; failed/racing attempted publication may consume a
definition, and callers create fresh definitions rather than replaying them across generations.
The caller owns the dedicated registry and any semantic executor resources in its runtime lease.

`McpPreparedDiscoveryIdentity.Capture(semanticReceipt, actualBinding, finalCatalog, exactPolicy,
reservedGeneration)` proves dedicated semantic admission, exact current snapshot/scope, canonical
metadata, actual prepared adapter/declaration, policy and final catalog. A pre-open receipt may
have an unbound catalog with no invocation generation: the reserved integer is metadata and grants
no invocation authority. `TransferRuntimeOwnership()` returns one `SessionRuntimeLease` containing
the same capture owners. It consumes each retirement-aware wrapper returned by the corrected
capture, so runtime release joins already admitted retirement instead of reentering lifecycle close.

After actual open, `BindOwner(owner, attachment)` is one-shot. It obtains the real catalog through
`owner.CaptureToolCatalogRegistryForBinding(attachment)`, validates every identity against the
actual attachment generation/catalog, binds every capture, then opens semantic execution fences.
The fences verify exact current attachment/lifetime, native session generation and scope owner.
Binding failure consumes admission permanently; the coordinator keeps the target unavailable and
joins partial bound/unbound cleanup under its actual reservation. Normal hooks and input remain
blocked until binding succeeds. No old closed-generation adapters are replayed for history.

Required composition dependencies are corrected pre-open capture `08b43be1e68995b41a75a3a2b12fdc57c2ca327d`,
its `UsesCapturedToolBinding` partial, root private wrapper provenance getter
`9f5a83e3b43003686b338e2ac83faba50105829b`, and root's actual reserved-generation factory/binding
catalog seam. Root owns ordinary profile/config/permission integration, SessionRuntimeLease
coordinator binding, resource aggregation, shared test registration, compilation and qualification.
The runtime stop-order correction `8b8b98083d193a4c9eb9e8f1eec8eadcdc02be0b` is separate and must be
reviewed/composed; admitted HTTP stop and notification-disconnect ownership remain parity gates.

Fixtures cover metadata impostors, affine semantic receipts, substituted adapters/policy,
reused registrations, missing requirements, config prevalidation, stable empty transfer, held
earlier cleanup with original faults, and actual owner binding plus native final-policy denial and
held semantic invocation. Provider/process/server fixtures remain synthetic and never execute
external programs or network requests. No trust override, package resolution, build or test ran.
