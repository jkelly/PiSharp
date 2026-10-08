# Native string-object schema profile

The published native CLI admission path accepts a bounded object schema containing string properties. `NativeStringObjectSchema` reads each registered descriptor's existing JSON parameters; `NativeExtensionActivation` advertises the original parameters and uses this validator during both initial and final prepared-action validation. Admission does not rebuild a descriptor's schema or modify arguments.

The supported nonempty root requires `type: "object"` and an object-valued `properties`. It permits `description`, `required`, and boolean `additionalProperties`. Each declared property requires `type: "string"` and may include a string `description`. Descriptions are annotations, preserved in declarations. Omitted `required` and an empty `required` array both make all declared properties optional. A present declared property still needs a string; optional null is rejected.

Omitted `additionalProperties` and explicit `true` allow additional argument properties, including nested opaque values and explicit null. These values survive validation unchanged. Explicit `false` rejects undeclared properties. Required/property matching is ordinal and case-sensitive. The previously supported empty schema `{}` remains unconstrained within object arguments; the previously supported closed string-object schema remains closed.

Unknown schema keywords fail admission with `UnsupportedSchema`. This includes validation keywords such as `minLength`, `pattern`, `enum`, `anyOf`, `$ref`, and `minProperties`, and additional annotation keywords outside this profile. Schema-valued `additionalProperties`, non-string declared property types, absent/malformed `properties`, invalid `required` entries, and duplicate required names are unsupported. No keyword is silently ignored to gain execution.

The schema cap is 16,384 serialized UTF-16 characters, with at most 32 properties and 32 required entries. Declared property names contain 1–128 UTF-16 code units, scalar Unicode, and no control characters. Descriptions contain at most 4,096 UTF-16 code units and scalar Unicode. Strict owned JSON admission rejects duplicate object keys and an escaped unpaired-surrogate property name before schema validation. An escaped malformed string value can be owned as JSON syntax but fails scalar string decoding and profile validation. The tests observe these distinct boundaries explicitly. The enclosing tool invoker retains its existing argument, action, result, Unicode, finite-number, depth, and resource limits; this profile does not replace those checks.

There is no conversion or cleaning. Numeric, boolean, null, array, or object values do not become declared string values. Unknown argument properties are retained or rejected according to `additionalProperties`; they are never removed. An authored invalid replacement must pass the same final validator before native authorization or execution. Approved tool names, module hashes, manifest, scope, generation, and durable configuration remain separately enforced.

## Authored native Hello exercise

The reference for the public JSON shape is upstream Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`, `packages/coding-agent/examples/extensions/hello.ts`, SHA-256 `0aa4e9800c2526914d4c1edb00b2cfa9bd9dd6da5218289994cccd5f5bfa4934`. Its public tool name is `hello`, its description is `A simple greeting tool`, and its parameters are:

```json
{"type":"object","required":["name"],"properties":{"name":{"type":"string","description":"Name to greet"}}}
```

The new `PublishedCliFixture.HelloSchemaEntry` is explicitly authored C#. It registers that schema unchanged and returns Hello's text and details shape:

```json
{"content":[{"type":"text","text":"Hello, Ada!"}],"details":{"greeted":"Ada"}}
```

It records the actual `IExtensionToolInvocationContext.ToolCallId` and received arguments separately through the existing task-local fixture marker sink. Opaque input is therefore observable without adding properties to Hello's result. A separate authored call hook substitutes a numeric name only for the `fixture-invalid-replacement` sentinel, testing final schema rejection. That hook is test instrumentation and is not part of the original Hello extension.

`NativeStringSchemaTests.Cases()` supplies four direct production-class groups for preserved input/annotations, optional and legacy behavior, unsupported schemas, and exact bounds. `NativeExtensionSessionCommandTests.HelloSchemaCases(host, cli)` supplies three provider-specific compiled CLI groups and one rejection group. Root wiring makes the existing session test class partial and registers both case lists. The existing published PluginCli project includes the new fixture source automatically; no new dependency or runtime is needed.

The CLI groups use explicit separately approved package manifests and artifact hashes. Both create and independent prompt/resume/branch use the same enabled-tool arguments. They exercise each existing Responses, Anthropic, and Completions request factory, injected offline HTTP handler, literal stream parser, actual registered callback, final extension target authorization, acknowledged durable checkpoint, reopened store, selected branch, package disposal, snapshot cleanup, and physical child-process/stream joins. The first request's complete authored expected HTTP body checks the schema and description through the actual provider declaration mapping; the initial durable loadout also preserves the original parameters. Responses callback IDs are the exact authored `call_id|item_id`; the other two providers retain their authored call IDs.

Registered native input admission commits user text blocks. The Completions expected first body therefore contains a user content array of `{type:"text",text:...}`; the extension-free session helper's string-content expectation does not apply to this route. The full body predicate remains exact. The durable session registry must also forward optional invocation-aware adapter dispatch, preserving the original call ID and identical authorized final action through its named adapter wrapper.

The rejection group checks an invalid initial name, an invalid hook replacement, an unadvertised/ungranted Hello tool, and a native write with no file grant. The zero-grant case is an unknown-tool rejection, not evidence of a final extension-policy denial. The write case reaches the actual final native policy and proves no file was created. Both schema cases prove the Hello callback never executes; the final-replacement case also proves the registered hook ran. Errors remain durable and acknowledged. All cases reuse existing output caps, deadlines, process joins, and owned test cleanup.

These groups require root compilation and execution before any pass claim. They launch no Node process and make no network/provider call: the existing injected HTTP handler validates actual request bodies with its explicit inert auth value.

## Remaining compatibility work

This profile and fixture do not execute unchanged TypeScript, Jiti, the public extension loader, TypeBox, or upstream `validation.ts`. TypeBox 1.3.27's live nonenumerable `~kind` metadata is distinct from the upstream validator's `Symbol.for("TypeBox.Kind")` fallback test. Neither hidden metadata nor the upstream Convert/Compile/optional-null behavior is reproduced here. Plain-string execution is the admitted native scope; future preparation requires genuine whole-source observations and an explicit trusted preparation contract, with initial/final native validation and authorization retained.

The whole Hello source's actual five-argument execution and the three original Hello corpus IDs remain separate qualification requirements. All 11 original extensions and 42 scenarios remain mandatory; authored native tests add no executed-corpus credit. Raw relative tool-call argument fidelity also remains open: existing native path preparation passes validated absolute arguments to registered hooks. This schema admission does not alter that boundary or claim full schema/provider/source parity.
