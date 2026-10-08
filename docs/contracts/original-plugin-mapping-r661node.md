# Bounded original-plugin mapping

This new, independent adapter makes the legacy and current original package names
share the same supplied export references. The native prepared bridge's controlled
virtual-module replacement currently contains only the current package names. The
pinned original `virtual-modules.ts` maps both families to the same namespaces.

`createOriginalPluginMapping({ Type, defineTool })` consumes caller-supplied exports
and exposes only six specifiers and the `Type` / `defineTool` named slices. It does
not import packages, acquire dependencies, read credentials, change existing loader
admission, or install a module map in the live bridge. The caller is responsible for
the supplied exports' provenance. Neighboring exports and module paths refuse.

`activateDefaultPlugin(module, api)` is a consumer for an explicitly supplied ESM
default factory. It returns the factory's original result directly, preserving a
returned Promise reference and a synchronous exception reference. It does not
implement Jiti's default import, original extension load transactions, ownership,
commit/rollback, or a new callback lifetime. Those remain the existing host's work.

Five authored synthetic groups cover alias reference identity, a hello-shaped tool
consumer, bounded export refusals, single default acquisition, and original fault /
Promise identity. They are unexecuted. Synthetic Type/defineTool inputs are clearly
test doubles; no unchanged upstream TypeScript execution is claimed.

Original source: Pi v0.99.1, commit
`d86654abb8862e201933517d6f1fce9f88dd117f`:

- `packages/coding-agent/src/core/extensions/virtual-modules.ts` (lines 14–35):
  original current and legacy namespace aliases.
- `packages/coding-agent/src/core/extensions/types.ts` (lines 651–655):
  `defineTool` retains its argument identity.
- `packages/coding-agent/examples/extensions/hello.ts` (lines 5–29): Type,
  defineTool, default factory and tool registration shape.
- `packages/coding-agent/src/core/extensions/loader.ts` (lines 558–570, 601–606):
  actual Jiti/default-factory and asynchronous transactional initialization; not
  replaced or claimed here.

Base is combined `a9df14f95d530157de021da81bc9fd17f69e2fd0`. The separately reviewed
`928f569` prepared revision repair is absent and must be composed before any future
live prepared-profile use. No previous Node qualification transfers to this leaf.

## Allocated StringEnum and truncation successor

R814ROOT allocation (SHA256 c33c1d2956fa26b3b59bb2b68ab11bb1dbb5989a55ba4b78168a6d3b163384cd) releases only the four mapping files and new own docs/tests under the mapping directory. Base 587d2b6458dc92bacaee9f13a4df9e2b2efc02cf. The shared live loader, prepared revision, native host, original package/module lanes and Session/Agent files are excluded; no such file changed.

`createOriginalPluginMapping({ Type, defineTool, StringEnum?, truncation? })` accepts optional explicitly supplied ORIGINAL exports. StringEnum must be a function and is stored directly in the one frozen AI slice shared by current/legacy aliases. Absence preserves the old Type-only slice and continues to reject StringEnum lookup. No function wrapper, schema recreation, default export or capability stub is supplied.

The next concrete transferred export is the pure public truncation slice. The supplied original `truncation` object must own DEFAULT_MAX_BYTES=51200, DEFAULT_MAX_LINES=2000, formatSize, truncateHead, truncateTail and truncateLine. Functions are captured once, directly, into the shared frozen coding-agent slice. Missing/inherited/wrong fields reject before consumer invocation. The source object may contain implementation-only exports, but GREP_MAX_LINE_LENGTH and truncateMiddle are never forwarded because pinned coding-agent root does not export them. Original result, error, function and alias identities remain intact. Export values are shape checked, not authenticated; the existing owner must supply and prove admitted real exports. No package import/acquisition occurs in this adapter.

Pinned StringEnum implementation is packages/ai/src/utils/typebox-helpers.ts:14, 789 bytes, SHA256 caf756258ce20c793dc8002cb8130a8efce18a7b5262ba0c54832b96f5a76e9f. It uses original Type.Unsafe, preserves string enum values and only adds truthy description/default fields. Original todo, tic-tac-toe and subagent use this export. Pure truncation implementation is core/tools/truncate.ts, 9255 bytes, SHA256 8e4507c3ed7ca7548cf7c2d7f77d07f2ae63a38b756789cbb44e9062db6c8762; no imports and no effects beyond strings/Buffer. Public root paths and exact five plugin import clauses are retained in original-import-comparison.json.

Count correction: the pinned virtual-modules.ts (2010 bytes, SHA2569a6cdfd7c21fd38fcf1e885990886e38a45f0f0355b918a4bbd757f460470622) has20 actual specifiers. R814 and an earlier read-only summary said22. The complete20-row comparison is now recorded; six specifiers have supplied slices, fourteen remain unavailable. Full original namespace/ABI/type/Jiti parity is open.

Authored tests are UNEXECUTED:

- Existing five synthetic controls remain. Five new forwarding controls check StringEnum reference/argument/schema-result identity, absent/invalid export refusal, fault identity/no eager invocation, public truncation alias/result identities and capture/field validation. These spies do not implement or qualify the original algorithms.
- `original-export-contracts.mjs` exports `originalExportContractCases(admitted)`. The coordinator supplies its reviewed genuine `{ Type, defineTool, StringEnum, truncation }` objects, receives seven named controls and must invoke/join each control's `run()` once and retain exact outcomes. Missing required original suppliers fails instead of silently skipping. Controls exercise actual enum schemas/options, shared import identities, default constants/size formatting/public boundary, empty/trailing-newline/inclusive limits, oversize first line/UTF-8 tail and line truncation. No alternative helper algorithm is embedded.

No original TypeScript loading, plugins or controls executed here. Whole todo/tic-tac-toe/subagent/truncated-tool/gondolin acceptance remains open: their other imports, TUI/custom renderers, process/filesystem effects, native session callbacks and ownership are not supplied by these exports. Unsupported custom rendering must remain explicitly diagnosed by its actual loader/owner.

Exact next coordinator join: supply r296's original Typebox/AI/tool module admission and caller-export identity evidence; allocate the shared actual-loader integration and reference/pin successors separately; run the ten forwarding groups plus seven original-supplier controls under a reviewed same-context packet. No fresh dependencies are needed for the supplied StringEnum/truncation adapters; evaluating genuine source still depends on existing module admission, which this allocation does not confer. TUI remains a documented next boundary needing get-east-asian-width1.6.0 and wider Box/TUI dependency ownership; no access improvisation occurred.

## Allocated bounded TUI successor

Parent instruction extends owned mapping work to direct forwarding of genuine Key, matchesKey, Text, Box, visibleWidth and truncateToWidth. Optional tui must own the complete six-export slice. Each value is acquired once and retained directly; a single frozen namespace serves current/legacy pi-tui aliases. Missing, inherited or incorrectly shaped fields reject. No renderer, default export, wrapper or substitute implementation is created. Optional absence retains the prior six-specifier map; supplied TUI adds two keys. The injection helper explicitly accepts originalTui and preserves full Typebox compile/value namespaces, for12 total keys with TUI.

The prior dependency statement above is superseded: the actual canonical live oracle already admits get-east-asian-width and marked among15 packages. The current live virtual map supplies zero original TUI objects, so the original-module owner must still authenticate/provide those objects. Shared loader/admission/pin/native files remain unmodified and require transfer or owner implementation. Source-only live-integration-plan.md and tui-forwarding-and-corpus-admission.md identify exact files and pins.

Five new synthetic controls cover alias/namespace/default boundaries, supplier refusal, capture/no eager invocation and composition identity. Three new originalTuiContractCases controls require reviewed genuine namespaces and cover aliases, bounded namespace/default absence and constructor-prototype/Typebox identity. All are authored and UNRUN. Together there are15 mapping synthetic groups,4 injection groups and10 genuine-supplier controls. Recursive corpus inspection found no unchanged StringEnum/truncation consumer without renderer requirements within the current package closure; Gondolin's no-explicit-render entrypoint imports an unlisted VM package. Full unchanged-plugin and rendering acceptance remain open.

## Exclusive live source successor

R829ROOT transfers the exact six loader/admission/pin/launch paths on95e73df; the mapping chain is now composed there and the actual production source route is implemented. New live-original-injection-handoff.md records supplier paths/pins, legacy-route preservation, native command-plan hash join,4 fresh-process original source cases and unchanged todo/truncated-tool diagnostics. Optional original withFileMutationQueue is additionally forwarded by reference for truncated-tool's genuine import;2 new synthetic controls are UNRUN. Full plugin lifecycle/renderer transport and actual qualification remain open. Earlier source-only mapping allocations above remain historical; no frozen bundle/handoff was overwritten.
