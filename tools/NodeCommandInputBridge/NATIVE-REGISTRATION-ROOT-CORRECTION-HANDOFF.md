# Native registration 51-root correction

Correction base: aaeec129b80b83742001b6c0698268a16deb3577. The independently verified Node evidence remains scoped to source candidate 44f2337d0141d3d29868403fb4b939ad65dac9e9: 26/26 unit controls and 7/7 pinned getter comparisons. No runtime receipt is extended to this registration correction.

## Fixed blocker

Assert-NativePreparedRootDeclaration still required 50 roots after registration/preparation moved to 51. It now requires exactly 51 unique required and declared roots, with the same case-sensitive exact-set comparison. The diagnostic names the no-worker metadata root and all original roots. Get-NativePreparedValidation uses this check for prepared launch admission.

The target registry remains unchanged: all prior29 records and prior50 output roots are preserved, with exactly one metadata-only target/root added (30 targets,51 roots). This correction does not modify source metadata translation, module/package allowlists, fixtures, solution, project locks or held providers.

## Audited preparation, registration and CI paths

A full source search of PowerShell scripts and CI/configuration files found two active production root-count gates:
- tools/prepare-native-products.ps1 already requires51, then calls Get-NativePreparedValidation for its completed receipt.
- tools/native-companion-registration.ps1 now requires51 unique roots in Assert-NativePreparedRootDeclaration. Get-NativePreparedValidation and prepared launcher revalidation route through it.

tools/test-native.ps1 (native smoke/CI preflight) calls Get-NativePreparedValidation before process setup. tools/test-native-sdk-admission.ps1 also calls it. Neither has a separate numeric root gate. No .github workflow exists in this checkout. The root enumeration function itself is unchanged. Historical selectorOwnershipIntegrationBasis.newRoots=50 records the earlier focused ownership integration and remains preserved; it is not an active gate.

## Authored admission controls

The default/full/partial selection and receipt assertions now match30 targets. Anthropic, authentication and focused-ownership root-addition controls now expect current51 roots and50 when their one respective target is excluded.

Nine new controls (92-100) cover the exact full51 set with all prior50 roots and only the metadata addition, order-independent acceptance, missing metadata root, duplicate declared root at count51, substituted declared root at count51, missing required root, equally duplicated required/declared sets, extra declared root, metadata-only selection, and rejection of an attempted worker argument. These augment the existing controls without executing products.

Only syntax parsing and source/registry/root-set comparisons were performed. No control script, Node, dotnet, native worker, build, API or provider process ran. All new and updated admission controls and the five native metadata groups remain unexecuted.

## Review and next allocation

Independent review must cover this correction and the updated selection controls. The coordinator must refresh the exact source manifest/review/allocation/build/prepared-admission/launch chain before runtime. The older namespace registration proposal and integration seal remain ancestor evidence; this correction commit is the authoritative gate/control successor.

Node-free native installation and the metadata-only registration guard remain intact. Original extension prepareLoadout coverage remains absent. Held Mistral/Azure branches remain frozen.
