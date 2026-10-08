# Explicit startup model and thinking preferences

This bounded CURRENT slice builds on merged main
`8f6dd81e3dea8346af73d5d51a6b0735c5f66b18` and the existing explicit
[user/project settings capture](settings-queue-startup.md). It is source-only;
the coordinator must register and execute the authored cases before qualification.

For `session rpc --live`, omitted `--provider` and `--model` now use merged
`defaultProvider` and `defaultModel` respectively. Each explicit CLI value wins
over its settings field. Both resolved identities must identify an existing chat
model in the same byte-pinned OpenAI, OpenRouter, or Anthropic catalog and API
already supported by the live host. The existing output-token validation applies.
Missing, invalid, unknown, or unsupported identities fail before provider
acquisition. Settings never supply endpoints, credentials, provider registrations,
tool permissions, extension approvals, or project authority.

Example user JSON:

```json
{"defaultProvider":"openai","defaultModel":"o3","defaultThinkingLevel":"high"}
```

An explicitly selected project file can override the model or thinking level.
`--model gpt-4 --thinking high` selects that model and clamps thinking to its
actual binding (`off` for this non-reasoning model). A per-model preference uses
`"modelThinkingLevels":{"openai/o3":"off"}`; this clamps to `low` because
the admitted o3 binding supports `low`, `medium`, and `high`, without `off`.

New-session thinking precedence is CLI `--thinking`, then the nonempty
`modelThinkingLevels[provider/model]` string, then `defaultThinkingLevel`, then
`medium` when a settings capture is selected. Selection clamps through the
ordered native levels, searching upward first and then downward, using the
actual session binding's capability list. Without settings and without the CLI
option, the existing registry default remains unchanged. CLI values must be
`off|minimal|low|medium|high|xhigh|max`; a CLI-only thinking option does not read
settings files. The native profile also applies this thinking selection to
scripted hosts, whose supported capability list governs the result.

Open/resume retains restored thinking unless the CLI explicitly requests a
level. Existing lifecycle validation of the recorded model remains intact: the
host still admits one selected live model, and a mismatching recorded model
fails. Defaults do not register additional historical models or silently replace
history. Chat/terminal forward to the shared host; their default-terminal
product flow has not been independently qualified by this slice.

The pinned source evidence is Pi v0.99.1
`d86654abb8862e201933517d6f1fce9f88dd117f`:
`packages/coding-agent/src/core/sdk.ts:205-268` (resume, per-model/global/CLI
thinking selection), `settings-manager.ts:800-804,865-892` (default getters and
provider/model keys), and `packages/ai/src/models.ts:1228-1246` (clamp order).
The supplied local semantic-oracle source was read; no oracle was executed.

This explicit-file native profile retains its prior limitations: no automatic
agent-directory/trusted-project discovery, automatic auth-capable provider
fallback, scoped/fuzzy model patterns or thinking suffixes, settings persistence,
reload, interactive settings UI, or automatic preferences after replacement.
The source's default medium is introduced only for selected settings captures;
no-settings invocations preserve the existing native default. Invalid setting
types are rejected rather than reproducing every JavaScript coercion. Existing
history without a thinking entry retains the native restoration behavior rather
than adding a new fallback policy. Nonselection fields remain inert data.

Six existing-framework test groups cover pinned selection and overrides,
capability clamping without universal off, actual RPC startup, durable resume,
and invalid admission. Actual host fixtures inject an inert key and a handler
that fails on any send; they never read environment credentials or call APIs.
Their original hosts are directly awaited and joined on every fixture exit.
No runner registration, native build/test, package changes, or live calls were
performed here.
