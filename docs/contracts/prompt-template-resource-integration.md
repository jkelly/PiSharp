# Caller-supplied prompt catalog and input admission

This source-only CURRENT slice extends frozen prompt parsing/expansion commit
`967cb423d7c4779e1ee8cc3e0fe27c075b82cbe6`. Upstream remains Pi v0.99.1,
`d86654abb8862e201933517d6f1fce9f88dd117f`. Eight catalog groups and twelve
admission groups are authored, unregistered and unexecuted. No build, reference
execution or phase/source/release acceptance is claimed.

## Value catalog

`PromptTemplateCatalogBuilder.Build` consumes ordered `PromptTemplateReadOutcome`
values and the existing injected frontmatter decoder. A successful outcome
contains supplied complete text, leaf filename, file path and source identity;
a failed read contains the supplied diagnostic message. Paths and provenance
are data, never grants or instructions to open files. The builder does not
discover, canonicalize, stat, read or authorize resources. Project metadata does
not establish project trust. No settings, environment or user files are accessed.

Successful parses produce `PromptTemplateResource` wrappers around the existing
pure model, preserving supplied `FilePath` and `SourceInfo`. The source-info
fields retain path, source, user/project/temporary scope, package/top-level
origin and optional base directory. The builder captures immutable arrays;
later changes to the caller's input collection do not change the returned
snapshot. No resource-generation authority is inferred from a snapshot.

Read failures and parser/decoder exceptions produce file-level warning
diagnostics. Cancellation exceptions propagate unchanged, and caller-requested
cancellation is checked before enumeration, after decoding and before returning;
it never publishes a successful partial catalog. Enumeration failures remain
the caller's failures, not file warnings. Source diagnostics are supplied or
exception messages, with no new message redaction policy introduced here.

Name deduplication is ordinal and first-wins, in supplied order. Later same-name
entries produce the pinned `name "/NAME" collision` message with the loser path
and winner/loser collision fields. All parse/read warnings precede collision
diagnostics, matching the source loader's separate load and dedupe stages. No
alphabetical sorting, path deduplication or universal user/project precedence is
added. The owner must supply the actual resolved order.

**No native YAML decoder is included.** The injected decoder must retain the
pinned YAML parsing semantics before selecting string metadata; synthetic test
decoders prove only the boundary. Malformed YAML is tested through a supplied
decoder failure, not a hand-written YAML implementation. Full native YAML,
resource loading and diagnostic parity remain unqualified. Explicit missing
additional paths are errors at the outer source resource-resolution layer;
this builder handles already supplied read outcomes and does not fabricate
missing-path errors.

## Operation-specific input order

`PromptTemplateInputAdmission` implements existing `IPromptInputAdmission` using
an immutable template capture and borrowed input/command boundaries. Its
constructor requires `PromptTemplateInputOperation`, because input source and
streaming behavior cannot distinguish an ordinary prompt from an explicit
steer/follow-up command. `IPromptTemplateCommandAdmission` is supplied by the
owner; it retains actual registration identity, command parsing, execution and
error reporting. It consumes raw input using the extension-command syntax,
which is distinct from template argument parsing.

| Operation | Raw extension slash command | Template expansion |
|---|---|---|
| Prompt | Execute first when expansion is enabled; handled stops input | Enabled by default, after input reducers |
| Steer / FollowUp | Reject a registered command; never execute it | Always enabled, after input reducers |
| ExtensionMessage | Skip command dispatch by default; explicit opt-in allows it | Disabled by default; explicit opt-in enables it |

Prompt opt-out skips command dispatch and template expansion while retaining
input reducers. Explicit queue opt-out is rejected as a configuration error:
the pinned steer/follow-up route always expands. Unknown slash input reaches
reducers unchanged. A handler-transformed slash string is expanded as a template
once and is never sent back to the extension-command boundary. Handled decisions
stop processing before template expansion; reducer images use the existing
nullish fallback semantics. Template expansion preserves effective images.

Pass **raw input reducers**, not the CLI's already combined command/input
admission, as `inputHandlers`, or commands would be processed twice. The current
CLI command adapter and RPC dispatcher are not wired to these interfaces by
this slice. If the separately owned skill stage is later present, it must run
after raw input reducers and before this template expansion, as upstream does.
This slice does not implement skill expansion.

The adapter uses existing `PromptInputValue.Own` and `Apply` for bounds, scalar
validation and image ownership before/after transformation. Native bounds and
typed invalid-input errors remain explicit differences from source diagnostics;
a registered queued extension command produces existing native `InvalidInput`.
It neither materializes a transcript message nor queues or starts a run. The
existing session owner must validate and commit the returned decision normally.

Awaited command/reducer work remains the original awaited operation. Caller
cancellation is passed through and checked after awaits; no timeout proxy,
detached task, new cancellation source, callback generation or process owner is
created. An uncooperative borrowed callback may delay cancellation settlement,
and this adapter continues to await it. Original callback exceptions propagate;
the existing command/reducer owner retains source-specific failure reporting.

## Baseline evidence and limitations

The supplied semantic-oracle baseline manifest records the source identity.
These canonical files matched their recorded SHA-256 values during the
preceding read-only inspection:

| Source under `packages/coding-agent/src/core` | SHA-256 |
|---|---|
| `agent-session.ts` | `26e76e13456757b0419df26b1f4d9bce9faa28c2588ad66af3e4cc3e9b179e0a` |
| `resource-loader.ts` | `45daa19ad4aa08856cf1eb9b026230b918eb25e163c7dc9ab5cf8add6825955a` |
| `diagnostics.ts` | `1914431098db1b4638e4fb3582ac22e8125356c4ab024671494f565c6d5ffb39` |
| `source-info.ts` | `52648af6e9bb29858ece4eaeccedd960c5de8a0ac4ec25c0ee851ff00de27f5c` |
| `package-manager.ts` | `c571e652abec58e1302177137e6640c228afa1d68089afd904d3073693691863` |

Source anchors: resource-loader.ts:851-876/1148-1171 for load/deduplication;
agent-session.ts:1888-1925 for prompt ordering, 2087-2115 for explicit queue
ordering, and 2278-2305 for extension-message defaults. Adapted Pi algorithms
remain covered by the existing MIT attribution to Copyright (c) 2025 Mario
Zechner in `THIRD-PARTY-NOTICES.md`.

The real resource owner still must supply authorized selections: project trust,
settings/packages, conventional direct-child/ignore/symlink rules, explicit
nested paths, canonical path deduplication, diagnostic errors for missing
explicit paths, source metadata, reload and command listings. No authority or
complete resource feature is established here. No future v1.0.2 behavior is used.

## Tests and coordinated handoff

New test groups follow the existing CodingAgent console-runner convention:
`PromptTemplateCatalogTests.Cases()` and
`PromptTemplateInputAdmissionTests.Cases()`. They cover ordered snapshots and
collisions, warning continuation/order, supplied path identities, decoder and
enumeration failures/cancellation, ordinary versus explicit queue command order,
raw/transformed/handled input, image preservation, opt-in/opt-out policy, native
bounds, and joins of original pending callbacks after caller cancellation.

Tests have not been compiled or run, and runner registration is untouched.
The execution coordinator owns native execution. CLI/RPC composition belongs
to the composition owner, native extension boundary adaptation to the extension
owner, and TUI completion and end-to-end tests to their respective owners.
All existing source, project files, runners and
settings/skills/provider/extension code remain unchanged. This catalog/admission
slice is an unwired prerequisite, not a
completed user-visible prompt-template feature.
