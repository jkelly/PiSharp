# Explicit published native activation in offline session commands

This candidate makes the existing published native loader usable by the compiled `session create`, `prompt`, `resume` and `rpc` commands. `prompt`/`resume` retain report, print and JSON output. It is an experimental, explicitly trusted, Windows x64 `net10.0` published-package profile. It does not qualify arbitrary third-party code as safe, guarantee collection of arbitrary plugin load contexts, or close Phase 5/6.

Each modifying invocation supplies one package and separate documents, with exact tool names:

```text
--extension-package <absolute existing published directory>
--extension-manifest <absolute manifest JSON>
--extension-approval <absolute separate approval JSON>
--extension-snapshot-root <absolute existing directory>
--enable-extension-tool <exact registered name>   # repeat for at most 16 unique names
```

These flags are rejected by read-only inspect/tree. There is no directory discovery, persisted enablement, dependency restoration, source build, credential/environment discovery or live provider call. Existing commands with no extension flags keep their ordinary offline-family behavior. Create stores the combined native/published declarations; later processes must explicitly supply matching runtime bindings. A prior session record grants no executable package authority.

The approval is a closed JSON object:

```json
{
  "schemaVersion": 1,
  "execution": "ApprovePublishedFixtureExecution",
  "packageRoot": "<exact canonical package directory>",
  "manifestValueSha256": "<lowercase SHA256 of the retained manifest JSON value UTF8>",
  "artifactHashes": { "<relative artifact>": "<exact lowercase SHA256>" },
  "sourceScope": "Explicit",
  "effectiveScopeId": "cli-native-explicit",
  "policyRevision": "experimental-policy-0",
  "hostGeneration": 1,
  "sessionPath": "<exact normalized absolute session path>",
  "workspace": "<exact canonical workspace directory>",
  "snapshotRoot": "<exact canonical existing snapshot directory>",
  "enabledTools": ["<same ordered list supplied by CLI>"]
}
```

The caller's separate approval is the decision to execute. Hashes bind that decision to exact package inputs; integrity alone creates no approval. The manifest and approval are bounded to 256 KiB UTF8, 65,536 UTF16 characters and JSON depth 16, with strict duplicate-property ownership and fixed rejection text. Scope, policy, generation, session/workspace/snapshot-root paths, tool list, manifest hash and the complete artifact set must match. The unchanged manifest reader rechecks TFM/RID/features/capabilities, artifacts and path/integrity rules; the unchanged loader validates published PE/dependency/runtime metadata before constructors. Native overrides and command registrations are unavailable in this profile. Missing approval, mismatched identity, duplicate/reserved enabled names and failed artifact preflight happen before executable load and durable acquisition.

Actual registered tool schemas and names become knowable only after initialization. The transactional activation then validates them before acquiring a durable session. Unknown enabled tools, unsupported schemas or registrations outside the supported typed-tool/input/result/observation profile roll back and join package cleanup. This does not undo arbitrary side effects of trusted constructors or initialization. All admitted schemas must be either `{}` (unconstrained JSON object arguments) or the complete closed-object/string-property subset: `type:object`, `properties` containing only `{type:string}`, `required` containing unique declared names, and `additionalProperties:false`. There are at most 32 properties; unknown keywords, types and richer constraints reject explicitly. General JSON Schema support remains required work.

The CLI owns one loader, actual transactional registry, captured `ExtensionAgentBinding` and input admission object. Enabled captured adapters and declarations enter the same `SessionRuntimeRegistry` as built-in file/Bash tools. The registry constructs one invoker using the binding's same captured prepared hooks. A call receives initial mandatory adapter validation, registered call hooks, repreparation through the same resolved adapter, full final validation and one mandatory final policy. The policy allows an extension action only for an enabled captured name with its exact owner/generation/registration target, `invoke` operation, object arguments and empty command/cwd/environment fields. Native actions retain the existing explicit file/Bash policy; enabling a plugin never grants native file/process access. Source-shaped result patches retain the accepted content/details/structured/null/outcome separation. Hooks cannot authorize effects.

At the source after-hook boundary, nullish `details`/`usage` retain the original result values (`hook.details ?? result.details`); this differs from the intermediate reducer's explicit-null observation. Accordingly, the fixture's `details:null` patch preserves exactly `{"argument":"redact"}` in both the complete public tool result and canonical durable result. Its non-null content replacement still clears stale structured output, while the tool's original opaque fields and independent `future:null` remain in the complete event result.

One-shot plugin input uses the actual coordinator `SubmitInputAsync`; RPC passes the same captured input admission to its dispatcher. Transformed text is the effective canonical user input. Handled input starts no run, consumes no coordinator message ID/clock, appends no message and alters no queue. A handled print produces no previous-generation text. Report output adds `inputDisposition` only for submitted plugin input; the default non-plugin report is unchanged. RPC preserves distinct started/queued/handled responses, normal shared JSONL output and asynchronous settlement through its coordinator admission seam.

Durable committed input/assistant/tool-result barriers stay ahead of callbacks, native effects and later provider requests. One-shot report/print occurs after session writer and package cleanup. JSON/RPC events retain their actual awaited streaming barriers; host success still requires durable settlement and owned cleanup. The profile's shared async disposal joins the loader and registry even when cancellation callbacks throw. Callback faults produce the accepted bounded tool failure; diagnostics never copy plugin exception text. Output faults, abort and EOF join admitted work and loader leases. Failure or uncertain cleanup is not replayed and may require restart/inspection. Lexical/path checks and collectible contexts are not a hostile-filesystem or untrusted-code sandbox.

`PluginCli` is an authored separately published consumer of the real ABI. It registers a closed-string echo tool, input transform/handled callbacks, tool replacement/schema-failure/block controls and result redaction. Module/constructor/initialization/disposal markers use fixture-only task-local environment variables, not product configuration. Its held callback uses a bounded task-temp gate directory and actual file-watcher/CT cleanup; no sleeps establish ordering. Six new native test groups exercise actual compiled CLI processes, real immutable hook outcomes, all three offline HTTP/SSE families, public print/JSON/RPC bytes, durable acknowledgement/reopen/selected branches, preactivation rejection, final-policy denial, cancellation/EOF and an actual broken stdout pipe. These tests are authored and not executed by the implementation owner; root build/test evidence is separate.

Remaining mandatory work includes the complete schema/extension surface and event families, lifecycle/custom state/commands, provider/auth/UI adapters, source mutable-alias/context-abort/own-undefined semantics, general discovery/enable/disable workflows and the platform/identity/reload qualification matrices. This candidate's native syntax, experimental approval disposition and immutable hook profile are explicit and are not a whole-Pi CLI or SDK parity claim.
