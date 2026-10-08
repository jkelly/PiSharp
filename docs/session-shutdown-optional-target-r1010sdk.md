# Optional target file in actual session shutdown

The real Profile publication/replacement control reached the native shutdown dispatcher after its separately published minimal plugin loaded. In-memory replacement has no `SessionFile`; the CLI binding serialized `targetSessionFile: null`, which the dispatcher correctly rejected as `InvalidDescriptor`.

The CLI binding now adds the optional field only when the target has a file. The strict dispatcher, core session replacement, security/import policy, and approvals are unchanged. A target with a file still carries that exact string, with the original `session_shutdown` type and mapped reason.

The existing publication case keeps its genuine volatile replacement path and also performs the same actual profile/loader/catalog/MCP replacement lifecycle with ordinary local storage. The pure shared-contract plugin registers a real `session_shutdown` observation through its initializer; its explicitly admitted callback records the dispatched data. The control verifies absence for the volatile target and exact existing local file identity for the persisted target. Both routes retain all original tasks and join profile/session cleanup. The eleven registered groups are unchanged.

This is source-only preparation. The previous R1001 failed original and its full fault inventory remain historical evidence. Since CLI production changed, no prior fourteen-product or forty-nine-runtime source-equivalence reuse is claimed: the upcoming eighty-seven-control qualification requires genuine fresh production products.
