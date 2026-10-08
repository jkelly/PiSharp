# Native namespace preparation witness correction

Successor base: `f5482d7153b797b9182ed5371e81077b41f02762`. Production files changed: zero. One existing NamespaceAuthority group is corrected and strengthened; the namespace suite remains four Extension Contract groups and one actual CodingAgent profile group.

## Existing runtime evidence

The coordinator's `artifacts/core-f5482d71/extension-namespace-results.json` reports three passing extension groups and one failing authority group. SHA256: `4fda28518260d4ae477c07031fabd5b421c4216b821c3b7a9ac5e71160ce6b9d`. The failure is the combined assertion "Real session preparation lost registered grouping metadata." It occurs before the execution-policy assertions. That report does not record the captured callback count or namespace values.

The coordinator's `artifacts/core-f5482d71/codingagent-namespace-results.json` reports the actual profile group passing. SHA256: `193fe64dcb6859fddfec48cd8f5be8c1e97aee4e0484967708271f6f73ea688d`. Thus the original immutable combined candidate has four namespace groups passing and one failing. These are coordinator results for the base, not execution or acceptance of this correction.

## Source diagnosis and corrected witness

Fixture.New configures PersistentAgentSession. ConfigureCoreAsync resolves the ordered active tools and prepares direct then outer against one shared loadout. Fixture.Owner then constructs ReplaceableAgentSession and binds the invocation owner. PersistentAgentSession.BindInvocationOwner constructs the generation-bound registry and resolves it again, preparing direct then outer against a new shared loadout. The source-derived total across both boundaries is four callbacks; the old test expected two after both. Four is a source inference, not a recorded runtime count.

The corrected test clears and checks captures independently at configuration and invocation-owner binding. Each boundary requires exactly two callbacks in direct,outer order, shared loadout identity within the pair, original hidden namespace identity, and unchanged declared/callable/registered ordering. It also requires a distinct loadout across boundaries while retaining the earlier captured namespace. Diagnostics report the boundary, actual count and callback names separately from metadata failure.

All original nested callable/refusal, final argument policy, declaration and activation assertions remain. An ordinary prompt must not add preparation callbacks. Explicit activation must prepare code exactly once while preserving registered direct metadata, model-only exposure and hidden-name filtering. Session disposal is owned even if the configuration witness fails before the replacement owner is created.

## Pinned source semantics and limits

Pi v0.99.1 remains pinned at `d86654abb8862e201933517d6f1fce9f88dd117f`. Its agent-session.ts _applyToolLoadout gives active callbacks one shared loadout per application and getNamespace resolves the exact registered tool name. This does not impose one preparation application for the lifetime of a native session. The native generation-bound invocation-owner preparation and immutable metadata contract remain unchanged. See [namespace contract](../contracts/native-tool-namespace.md) for inspected immutable upstream file hashes and bounded native differences.

No provider transport, terminal, production, source pins, dependencies or launch policy changes. No compiler, native build, test, SDK or upstream execution was performed here. Independent review and a fresh coordinator-controlled immutable runtime run remain pending. This correction closes no original milestone or phase gate.
