# Bounded ls installation in the real offline CLI session profile

Base: `5ab319781d3bca5d46ac938cc23e57ebe43874a6`, the isolated ls test-ownership successor of `0fe11c10152a3dc73750a0e2cbc0821ec14e596e`. This installation is authored source only; no build, test, SDK, provider or upstream execution occurred here.

## Source defaults and native installation

Pinned Pi v0.99.1 remains `d86654abb8862e201933517d6f1fce9f88dd117f`. [tools/index.ts at the immutable pin](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/index.ts) puts read/bash/edit/write in createCodingTools/createCodingToolDefinitions. Its read-only factories put read/grep/find/ls in their set; all-tool factories register ls. Inspected index.ts SHA256: `3c66ea1e03530fb85436ee639f35add8bf72718ac6e76b738a1f41be58a2b472`. The default coding set does not add ls automatically.

The real OfflineSessionProfile, shared by one-shot session commands and RPC/terminal callers, currently installs read/write and optional explicitly authorized Bash. Edit is implemented in PiSharp.Tools but is not installed in this profile; that original gap remains. No default set is expanded to claim full source coding/read-only parity.

The profile now constructs LsTool with its same LocalFileOperations instance and explicit canonical workspace/home. Its declaration and prepared adapter are appended to SessionRuntimeRegistry with Direct exposure and DefaultActive=false, before native extension registrations. InitialSystem toolsAdded still excludes ls. Existing default read/write order and optional Bash activation remain unchanged. Hosts/native APIs use existing SetActiveToolsAsync to select ls, persist the ordered activation, and restore it on CLI resume. No new command flag, terminal command, provider transport, process runner or package is added.

Native extension namespace/exposure/default-activation/preparation mapping remains byte-for-byte unchanged. New built-in ls has no synthesized namespace; all existing extension metadata remains on its original registration path. Existing namespace controls remain registered and directly awaited.

## Final policy and workspace boundary

Ls actions use the existing shared ToolInvoker and FilePolicy. The only added authorization condition is exact tool/operation ls, with action.Target in the existing explicit read-target set. All original Path-kind, cwd, empty command/environment, strict workspace-child and reserved-target guards still apply. CLI --allow-read accepts an absolute directory grant through its existing target admission path; the grant is exact, not recursive authority for other listing targets. No ambient workspace-wide grant is inferred. Workspace root, ungranted directories, outside targets and reserved session/script paths remain denied. Canonical alias handling and child-link containment remain the core adapter's documented profile.

Read/write/Bash branches and configured limits are unchanged. The CLI profile keeps its existing shared invocation/result and wire-message budgets, which can reject unusually escaped/large listings even when the core standalone ls factory has a larger result budget. This is explicit bounded profile behavior, not unrestricted source parity or relaxed limits for other built-ins.

## Authored composition controls

Three directly awaited groups under `native ls installation `:

1. ActualProfile: actual OfflineSessionProfile -> SessionRuntimeRegistry -> PersistentAgentSession -> injected offline Completions HTTP. Requires unchanged default tool set and original read/write schemas, explicitly activates ordered read/ls/write, checks optional path/numeric-limit schema and no namespace serialization, obtains exact real temp listing, checks canonical allowed final action, absent ordinary details and actual read/write results, and validates every provider request's ordered tool declarations.
2. WorkspaceDenials: the same actual pipeline refuses an ungranted inside directory, an outside-workspace directory still inside the fixture-owned temp root, and default workspace root. The transcript must retain error results without directory marker names. Existing reserved session-target grant admission also remains denied.
3. CliResume: prepares and disposes a real durable activated session, then calls actual SessionCommands.RunAsync in-process with a literal offline script and --allow-read directory. Requires restored ordered selection, exact canonical policy action, successful command settlement and durable two-entry listing/truncation notice/details observed by a fresh reader after all command/profile/session owners settle.

No child process or live network call is authored. All files belong to a verified unique temp root. Profile/session originals are awaited before fixture deletion; setup failure joins session disposal and preserves cleanup causes. The CodingAgent runner appends these cases and directly awaits their prefix rather than applying an outer timeout wrapper. Existing Tools ls ownership fixes and all other runner registrations remain.

Builds/tests executed here: zero. Three groups authored; compiler qualification, independent installation review and coordinator-controlled immutable runtime execution remain pending. The cleared core ls runtime allocation remains a separate candidate.

## Remaining scope

The core [native ls source mapping and limits](native-ls-tool.md) still applies: Unicode/default-locale ordering, platform/link behavior, unrestricted resource/numeric profiles, native/source error and cancellation differences, actual platform source differential and renderers remain open. Broad user-facing tool selection and complete Pi coding/read-only factories are not implemented by this registration; explicit activation is supplied by the existing host/native API and restored durable state. Find and grep remain unimplemented. No original milestone or phase gate is closed.
