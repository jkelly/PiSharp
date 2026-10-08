# Node tool namespace metadata handoff

Authored candidate from verified main b53c1774367b26d8c6d1ac1fa443d13fb691c07f. Original Pi v0.99.1 is pinned at d86654abb8862e201933517d6f1fce9f88dd117f. No Node, dotnet, original factory, corpus or test process was executed. No API calls, downloads, dependency/source allowlist expansion, credential/security changes or held-provider branch edits were made.

## Finding and implementation

Native ToolNamespace / ToolLoadout.GetNamespace already exist on the base. The optional Node registration facade rejected both namespace and prepareLoadout, so native metadata could not reach original preparation callbacks.

This patch admits bounded namespace strings and a retained prepareLoadout callback through the existing original registration transaction. Renderers and other unsupported fields still diagnose their surface. Tools retain default Direct exposure; exposure registration, defaultActive, annotations, other preparation hooks and richer tool metadata remain unsupported.

NodeToolLoadoutMetadata serializes the actual immutable native snapshot, never adapters/delegates or execution contexts. Declared/callable order is supplied as names; registered order, original declarations, exposure and optional namespace metadata are retained. Missing descriptions remain absent; empty name/description strings remain present. Unknown and ungrouped getNamespace return undefined; ordinal name matching includes hidden/inactive registered tools. Declaration members are shared by name across arrays. Each tool's namespace object is stable within a snapshot, copied and frozen. Cross-tool reference alias identity of equal namespace objects is not transmitted.

The original prepareLoadout function remains in Node and retains its definition receiver. A native weak-key identity and a bounded Node cache give callbacks of the same owner the same immutable facade for the same native ToolLoadout; later snapshots leave earlier captured objects intact. Bounds: 256 members per list, 128 snapshot identities per owner, 8 MiB retained snapshot JSON, 256 KiB snapshot/changes, 128-character tool/snapshot names and 65536-character text. Inconsistent ordered declarations reject rather than flatten their metadata.

The existing owner/generation/callback/operation admission for command-input.prepare and its handle-free preparation contract carry a strict preparationKind=loadout branch. No method/host callback allowlist changes, host context, execution function or nested tool capability is added. Existing settle/finalize/progress/UI ownership remains unchanged. Results admit only null/undefined, descriptions and hiddenDeclarations, leaving native presentation application and execution policy authoritative.

The native SDK contract is synchronous. Its registry-leased callback joins the pure worker request and its physical settlement using the existing asynchronous machinery before synchronously returning. It has no callback cancellation-token parameter; no SDK change or invented token is introduced. Foreign asynchronous preparation returns are unsupported: the original thenable is physically awaited before diagnosis. Worker lifetime/shutdown still owns termination; synchronous source hooks or unsupported never-settling thenables remain a trusted-worker liveness limit requiring coordinator review.

## Authored controls, not executed

- tool-loadout.test.mjs: seven grouped controls for exact lookup/absence/hidden tools, ordered immutable metadata and no execution members, same/later snapshot identity, bounded presentation-only results, joined asynchronous rejection, malformed/duplicate/oversized/execution-bearing snapshots and retention limits.
- admission.test.mjs: two new registration controls; the ancestor unsupported-preparation control now continues to reject prepareArguments/exposure/rendering while allowing this bounded prepareLoadout path.
- namespace-corpus.mjs: seven comparisons against only the exact getNamespace expression extracted from SHA-pinned original agent-session.ts using a timeout-bounded built-in VM over authored maps, plus one authored preparation callback. This is narrow original-getter evidence, not whole AgentSession or original extension callback qualification.
- NativeAdmissionTests/NativeToolLoadoutMetadataTests.cs: no-worker codec contracts for ordering, absent/optional/empty namespace, hidden exposure, immutable snapshots, presentation-only results and malformed/limit rejection. --metadata-only selects these controls; existing native worker tests run them first.

None of the four admitted original extensions currently declares prepareLoadout. This patch does not add an original extension to the source allowlist or claim actual original prepareLoadout corpus execution. A future independently admitted original hook fixture/source is needed for that qualification. Existing original Pirate/Hello and Commands/Input regressions must be run separately by the coordinator after clearance.

## Integration and remaining limits

namespace-admission.request.json freezes source/native mapping and exact candidate helper pins. The host supervisor owner must update the closed bridge plan helper rows (including tool-loadout.mjs), admission revision and worker/plan SHA constants before this candidate can launch. Its request includes the exact proposed plan. Existing immutable admission intentionally rejects these changed bytes until that owner integration. No denial is bypassed and no native launch has been attempted.

The original source/dependency/reference allowlists and qualified goldens remain unchanged. Existing corpus harness's approved-plan SHA also needs owner integration if that harness is rerun. Full native/Node builds, unit/corpus execution, original callback qualification and independent lifecycle review are pending; no old receipt or execution credit covers this patch.

The next missing metadata translations are ToolAnnotations and explicit registered exposure/defaultActive; all remain diagnosed at registration. Tool rendering, prepareArguments, output schemas, providers/MCP/virtual models, nested execution and unsupported lifecycle/UI APIs remain outside this slice.

The default native installation remains Node-free: no project references, package dependencies, CLI native routing or installation assets were changed. New runtime code is confined to PiSharp.Compatibility.Node and optional Node bridge files. Mistral/Azure artifacts remain on hold.
