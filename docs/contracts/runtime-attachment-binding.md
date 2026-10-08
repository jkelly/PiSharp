# Runtime attachment binding

Source only. No new build or test execution covers this successor.

`PersistentSessionLifecycle` accepts one explicit `runtimeForAttachment(cwd, generation, token)` factory instead of its existing registry or cwd-runtime factory. Initial create/open supplies generation 1. The serialized owning replacement path supplies checked current generation plus one before actual runtime acquisition and historical declaration resolution. Veto consumes and releases the fresh lease but does not commit the generation.

`SessionRuntimeLease` optionally captures one synchronous owner-binding callback. It is attempted once after lease claim, with the real owner and attachment. The callback must perform synchronous binding only; it must not launch unowned asynchronous work. Acquisition, transport, semantic implementation and cleanup remain explicitly admitted dependencies.

Use `PersistentSessionLifecycle.AttachAsync` for an initial lease requiring owner binding. Its initial reservation spans invocation-owner binding and runtime-owner binding. Replacement performs the same callback after committing actual Current but before releasing the target reservation or notifying AttachmentChanged/AfterReplacement. A reader may observe Current during this interval; native session operations remain unavailable under its reservation.

`CaptureToolCatalogRegistryForBinding` grants reserved read only through the exact active binding reservation, matching registration attachment and callback context. It delegates to the existing reservation's validation, including stopped admission, released/committed reservation, retirement and faults. Outside that callback it uses the ordinary session catalog API. It supplies no publication authority.

Binding failure permanently consumes the lease. Initial failure joins stops, reserved registered-resource cleanup and runtime release, retires the writer and disposes the owner before reporting original errors. Replacement failure identifies the actual committed target, joins registered and unbound transferred resources, marks the target retired before releasing its reservation and suppresses lifecycle notification. The previous retired session is also joined. This is a committed failed attachment, not a rollback to the old writer.

Four synthetic groups are registered once and directly awaited: serialized generation acquisition, fresh veto acquisition, reserved binding before initial return/replacement notification, and partial initial/replacement binding with two original stops including held cleanup. They use an in-memory session backend and a provider transport that must never be invoked. Exact API declarations and declaration-space/compiler hazards were inspected; all four groups are uncompiled and unexecuted.

Remaining composition: import corrected pre-open capture and activation/discovery leaves with the private adapter provenance hook; construct fresh admitted acquisition for each cwd/generation; wire the ordinary host profile to this factory and asynchronous attach; retain the separate runtime/HTTP/notification close-order gates. This seam does not claim MCP parity or supply ambient process, HTTP, credentials, discovery engines or settings authority.
