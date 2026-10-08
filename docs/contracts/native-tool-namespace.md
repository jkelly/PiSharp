# Native tool namespace metadata

Authored source-only successor of preserved `4429296e4135b53a38634a731fe7a9b3e43e5fad` for the original P6-06 namespace/GetNamespace gap. This slice adds grouping metadata and lookup; it does not implement MCP connections, codemode script execution, namespace discovery, annotations, provider transport or terminal behavior. No native/SDK/source/test launch or build was performed here.

## Source evidence

Pinned Pi v0.99.1 is `d86654abb8862e201933517d6f1fce9f88dd117f`. Read-only local evidence: `P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-reference-oracle-v0.99.1/upstream`; its detached .git/HEAD names that exact pin. No Git trust exception, security setting or upstream execution was used. Inspected file identities:

| Source | Bytes | SHA256 |
| --- | ---: | --- |
| `packages/coding-agent/src/core/extensions/types.ts` | 81892 | `de0ccce3b8b222d19a2ecef24902fc72aab62b6573129cbf886bc064dd6ab505` |
| `packages/coding-agent/src/core/agent-session.ts` | 155372 | `26e76e13456757b0419df26b1f4d9bce9faa28c2588ad66af3e4cc3e9b179e0a` |

[types.ts at the immutable pin](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/extensions/types.ts#L527) defines ToolNamespace with required name and optional description (527-532), ToolLoadout.getNamespace with an optional result (543), and optional ToolDefinition.namespace (593). [agent-session.ts at the same pin](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts#L1518) resolves the exact tool name through its definitions map. Namespace absence and unknown tool names both yield undefined; the registered tool can be inactive or hidden. The lookup argument is a tool name, not a namespace name or a guessed prefix. The source snapshot files were inspected, not executed or independently qualified as a clean checkout.

## Native contract and production propagation

`ToolNamespace(string Name, string? Description = null)` is an immutable record in PiSharp.Contracts. Optional Namespace properties on ExtensionToolDescriptor, ExtensionToolRegistrationInfo, SessionRegisteredTool and ToolLoadoutTool carry it. ToolLoadout.GetNamespace performs ordinal exact-name lookup in its registered captured metadata and returns null for an unknown/unannotated tool. Null maps source undefined; empty namespace names/descriptions and omitted descriptions remain distinct original values. No built-in namespace or inferred MCP name is fabricated.

The transactional registry snapshot preserves namespace values without executable delegates. ExtensionAgentBinding supplies them to its real leased preparation callbacks. SessionRuntimeRegistry supplies the same metadata for its ordered active-loadout preparation; OfflineSessionProfile maps actual native registration metadata into its real session registrations. Metadata is not serialized into model tool declarations, durable activation records, parameters, broker capabilities or final-action policy. Declared/callable/registered order, exposure, default activation, explicit activation normalization and original tool/policy/context lifetimes remain unchanged.

The existing native registration description/scalar limits apply to the two new metadata strings, which are charged to existing aggregate metadata accounting; direct session registrations charge namespace JSON to their existing declaration/metadata character budget. This is native resource accounting, not a namespace-based permission rule. Native bounded/Unicode-valid registration requirements are not claims of identical unbounded JavaScript input acceptance. Capture is immutable across registration removal/replacement and later binding construction; this follows the native fixed-binding ownership contract. Pi's lookup closure reads its definitions map synchronously; no hot-registry-refresh parity is claimed.

## Authored controls

Four registered Extension Contract groups, under the existing directly awaited `native tool activation ` prefix:

1. NamespaceLookup: actual preparation sees name/description, omitted/empty semantics, exact lookup, inactive hidden metadata, no tool/group-name confusion, unchanged three ordered tool sets and no namespace declaration serialization.
2. NamespaceCapture: actual transactional removal/replacement and a new binding show the new namespace; captured old registry/binding/loadout values retain their original immutable metadata and absent lookup.
3. NamespaceAuthority: real native binding and persistent session preparation/activation preserve metadata; normal nested calls retain callable classification and mandatory final-argument policy, with denied/hidden/model-only/inactive refusals and ordered activation intact.
4. NamespaceBudgets: existing per-string, aggregate registration and direct-session budgets reject oversized metadata without registration residue.

One CodingAgent group, directly awaited under `native namespace `: ActualProfile loads an explicitly approved authored Entry from the already prepared CodingAgent consumer graph using the existing manifest/approval/snapshot contract, then exercises actual OfflineSessionProfile mapping, native leased preparation, final request projection and persistent session/offline HTTP flow. Its callback requires the exact namespace before returning its presentation change. The test requires the projected description, unchanged canonical description and active set, and no tool/file effects. All session/profile/loader originals are disposed and awaited before deleting the owned fixture root. It adds no package, project, dependency, child process mode or preparation policy bypass. This is authored in-process future execution, not a native launch performed here.

Tests authored: 5. Builds/tests/native/source/SDK executions here: 0. Independent source review and exact integrated compiler/runtime allocation/qualification remain pending. The previously approved immutable candidate is preserved. Original source differential/package/P6 qualification, general registry refresh and other P6-06 functionality remain separate; no original phase gate closes.
