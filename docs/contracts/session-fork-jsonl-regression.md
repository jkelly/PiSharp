# Forked compaction JSONL regression

Scope: CURRENT Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`.

This slice adds one authored native case (three boundary variants) to the existing
framework-only session runner. It closes a test coverage gap across fork planning,
JSONL serialization, reload, tree metadata, and context projection. Inspection
found the relevant production behavior already implemented; no production source
change or format migration is proposed.

Pinned source proof:

- [createBranchedSession](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/session-manager.ts#L1632)
  removes historical labels, reconnects the path, maps a removed-label compaction
  boundary to the next retained entry, preserves a self-boundary, and appends
  globally resolved labels.
- [_buildIndex](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/session-manager.ts#L1103)
  restores the last physical entry as the leaf, including recreated labels.
- [buildContextEntries](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/session-manager.ts#L476)
  puts the latest compaction first and retains the range before it only when the
  first-kept ID occurs there. A self-boundary retains no earlier message.

The local supplied session-context oracle manifest pins this source commit.
Inspected `session-manager.ts` SHA256:
`450d82c529933214e088b8422f00061815e617f352f6c834689787a314064bff`.
These are source-derived expectations, not new original-Pi runtime observations.

The regression checks two consecutive removed-label boundaries and a self-boundary;
global labels written on an unselected sibling; the recreated label as physical
reload leaf; settings retained from summarized ancestry; exclusion of sibling and
summarized messages; Unicode content; exact opaque numeric tokens; and immutable
source wire records. It reads plan bytes through the existing bounded JSONL reader.
It does not acquire a writer or alter cancellation/process ownership.

Validation status: authored, not executed. Native build/test execution belongs to
the integration coordinator.
After its authorized build, use the existing Sessions runner with
`--filter "session fork JSONL reload" --report <owned-report-path>`.

Git denied ownership access to the canonical worktree; trust settings were not
changed. The isolated branch uses supplied full bundle parent
`bef2272a860ff2349dddbf4325e793edee595f34`. All production `.cs` files in
`src/PiSharp.Sessions` and the unmodified target test file matched the supplied
live candidate `75c18ddda73ce7d82954d701a582511d06a63dd1` checkout by SHA256.
The b55f3f07 incremental bundle could not be loaded because its prerequisite
`d761b863ba90164cf60c9d79fcef65c83a18183f` was absent from the imported bundles.
Apply only this two-file change to the integration candidate; do not merge the old
branch base as an integration decision.

Existing strict reader, graph admission, integer/Unicode and timestamp profile
limitations remain. This regression does not establish whole-session parity,
writer durability, original-Pi acceptance, CLI composition, Extras or future v1.0.2
behavior.
