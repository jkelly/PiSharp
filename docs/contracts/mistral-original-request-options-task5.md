# Mistral original hook request fields

The original `MistralChatPayload` has an unknown-valued index signature and the
awaited `onPayload` replacement is used before request wire conversion. Previously
the native adapter refused root fields such as `topP`, `randomSeed` and
`responseFormat`, although pinned Pi converts them and sends the request.

This slice preserves additional root JSON fields through that bounded hook seam.
It adds the original twelve camelCase-to-wire mappings and the object-only
`responseFormat.jsonSchema.schemaDefinition` conversion. CamelCase wins when
both spellings exist, including explicit null; already spelled wire fields pass
through. Schema property names are not recursively renamed. Conversion operates
on a separately parsed object and cannot mutate the retained `JsonData` callback
result. Existing core model/stream, transcript/tool admission, byte/depth bounds,
post-conversion byte bound, and original callback joins remain in force.

Pinned original: Pi v0.99.1 `d86654abb8862e201933517d6f1fce9f88dd117f`,
`packages/ai/src/api/mistral-conversations.ts`, SHA256
`5f6123383887a1b4ee8fb46fbd645b088e5da77984066497521d86661dde0fcd`.
Evidence: `MistralOptions` lines 38-42 exposes toolChoice/promptMode/reasoningEffort;
`MistralChatPayload` lines 80-93 admits unknown fields; lines 147-152 await and
select the hook result; lines 372-402 convert request/schema options; lines
433-439 implement source-present overwrite and object recognition.
Direct typed properties for topP/randomSeed/etc. are not added because that
original interface does not expose them. No generic samplingParams behavior is
invented for Mistral.

## Coordinator qualification packet

The new independent executable project is
`tests/PiSharp.MistralOriginalOptions.Tests/PiSharp.MistralOriginalOptions.Tests.csproj`.
Six groups use a caller-injected HttpMessageHandler and synthetic key; all HTTP
requests remain in that handler. Both NativeProviderFactory entry points are
exercised. No packages or test frameworks are added. Reports go to stdout and
include directly awaited group tasks, exception graphs, and both held originals.

Author validation is source-only: project identity/reference and nullable/API
signature inspection, exact source and base pins, and git diff checks. No build,
restore, tests, original Node execution or external API call was run. Runtime
qualification remains required under the coordinator's finite permission window.
Suggested finite run: build the new project and run its six groups once; rerun the
frozen six-group Mistral strict-tool project once to confirm grammar/raw schema,
toolsRemoved and held-task behavior. This document grants no execution permission.

Bounded existing differences remain: hook admission retains explicit model/stream,
transcript/tool structural requirements and JSON resource limits. Non-JSON values
cannot cross JsonData. This slice does not revise those contracts.
