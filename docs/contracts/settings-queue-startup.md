# Explicit startup settings and queue preferences

The subsequent [model/thinking startup slice](settings-model-startup.md) extends
this initial queue-only capture. Its documented selection fields now have
bounded effects; the remaining limitations below describe the original slice.

This CURRENT source-only slice starts from accepted main
`8a686c03ceac85a68ff2d5ecf1579ff12b35b02f`. It loads user and project JSON
settings, applies invocation overrides, and connects the resolved steering and
follow-up preferences to the existing persistent session APIs before RPC
publication. Chat and terminal reuse that host. No build or tests were executed.

## Usable flow and read admission

Append these options to an existing `session rpc`, `session chat`, or
`session terminal` invocation:

```text
--user-settings <absolute user settings.json>
--project-settings <absolute project settings.json>
--steering-mode all|one-at-a-time
--follow-up-mode all|one-at-a-time
```

All four options are optional and single-use. The two file options select exactly
the files to read. Each selected layer is read once, user first and project
second; invocation overrides apply last. Relative file arguments, duplicate
options and unsupported command-line mode values reject during existing argument
validation. No settings flags means no settings I/O and the prior startup path.
The existing maximum argument count and other option-value ownership are retained.

For example, user JSON with both modes `all`, project JSON with steering
`one-at-a-time`, and `--follow-up-mode one-at-a-time` start both queues in
`one-at-a-time` mode. Existing `get_state` exposes the resulting modes and
existing queue draining uses them; no new RPC metadata or dispatcher route is
introduced. Runtime `set_steering_mode` / `set_follow_up_mode` continue to use
their existing APIs. This capture applies to initial host startup, not reload or
automatic reapplication after session replacement. One-shot prompt/resume and
interactive `/settings` are outside this bounded workflow.

The flag authorizes the selected file read only. It does not establish project
trust, authorize sibling files or follow resource paths found in the JSON. No
implicit home/environment/project discovery is added. In particular,
`defaultProjectTrust`, `defaultProvider`, `defaultModel`, credential-like fields,
`defaultTools`, resource/package lists, `externalEditor`, proxy and command fields
are retained as data but have no effects in this workflow. Existing explicit
provider, plugin approval, tool file grants and Bash command admission remain
authoritative. File reads follow normal OS links; no containment guarantee or
new filesystem sandbox is claimed.

The pinned upstream [configuration contract](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/configuration.md)
loads `<agent-dir>/settings.json` and trusted project `.pi/settings.json`, with a
special pre-trust sessionDir read. That automatic discovery/trust workflow is not
implemented here. Explicit selected files make this native profile reviewable
without silently granting trust or widening execution authority.

## Pinned data behavior and differences

The authorized baseline
[settings manager](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/settings-manager.ts)
loads global/user before project, merges nested objects recursively, replaces
ordinary arrays/scalars/null with the later value, and applies overrides above
the merged settings. Each layer migrates `queueMode` to `steeringMode` only when
the latter property is absent; explicit null counts as present. Its getters use
`one-at-a-time` for missing/falsy queue preferences.

These rules are retained for JSON objects, including last-duplicate-property
wins before conversion to owned strict native JSON, a single leading UTF-8 BOM,
and missing/null distinctions. Unknown nested JSON is retained in an immutable
snapshot. The special top-level `defaultTools` data merge is also retained: when
both layers have arrays and the later array contains only `+name` / `-name`
entries, append it to the earlier array; other arrays replace. An empty later
array qualifies for this append when an earlier array exists. This is source
merge behavior, not permission to activate any named tool. Resource list getters,
all other migrations and all other settings effects remain unimplemented.

Missing or empty files contribute an empty layer. Read/parse failure contributes
an empty layer and a fixed `settings_diagnostic` on stderr, leaving other layers
and valid CLI overrides usable. Diagnostics expose scope/path/code, never raw
JSON or exception messages. Stdout remains the existing JSONL protocol. File
errors are not used to rewrite or create settings. Cancellation is propagated
and the original read is awaited before startup returns; no detached timeout or
retry proxy is introduced.

Explicit native limitations: roots must be objects, JSON parser depth is 64,
invalid truthy queue-mode values warn and fall back rather than being passed
through unchecked. Numeric representation and Unicode equivalence to JavaScript
remain unqualified. Missing/null/empty-string/false/zero use the baseline mode fallback.
Native object keys remain data rather than JavaScript prototype properties.
The file adapter reads raw UTF-8 bytes, removes no encoding preamble itself,
and the loader strips one BOM. Malformed UTF-8 equivalence is unqualified.
There is no source-compatible advisory lock, transactional multi-file read,
settings persistence, reload, home/agent-dir resolver or project trust UI.
Full settings parity is not claimed.

## APIs, evidence and qualification

`StartupSettings.LoadAsync` takes explicit paths, immutable overrides and an
injected `IStartupSettingsFileSystem`. `SettingsStartupConfiguration` validates
CLI options, captures settings and sends sanitized diagnostics. The existing
`RpcSessionCommand.RunAsync` uses the system adapter;
`RunWithSettingsAsync` follows the same original host lifecycle with injected
settings reads. Neither adapter owns sessions/providers/processes.

`StartupSettingsTests.Cases()` authors nine groups: nested/array precedence,
BOM/duplicate/migration/null behavior, failed layers, joined read cancellation,
actual injected RPC startup and captured state, actual physical JSON startup,
no-flags/diagnostic protocol handling, argument rejection before reads, and canceled host startup joining its
settings read. Host tests use the existing bounded connection and memory session
backend with an inert authored script; they make no provider requests. The
new owned fixture file is isolated from existing worker test fixtures. Tests
observe existing queue mode state; broader queue scheduling is covered by the
existing queue tests and needs coordinator regression qualification.

Coordinator registration must append this one collection and place the
`settings startup ` prefix in the runner's direct `await test.Run()` branch.
Its outer `WaitAsync` can detach original read/session cleanup and must not wrap
these cases. The host fixtures signal their own deadline cancellation and await
the original host task and connection disposal. No runner, test project, package,
lock, provider, RPC metadata, packaging or existing fixture file was changed.
All nine groups are authored/unexecuted. Coordinator owns registration, fresh
source/product admission, compilation and execution. Future v1.0.2 and Extras
settings work are excluded.
