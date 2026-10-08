# Selected prompt discovery and captured expansion

This bounded CURRENT slice is based on `bf9ed062f0bbcccb5e598a7b08269ceecc339d21`
and pinned Pi v0.99.1 `d86654abb8862e201933517d6f1fce9f88dd117f`.
It extends the existing parser, catalog and operation-specific input admission.
It adds no dependency, YAML implementation, provider call or shared CLI edit.

## Behavior and ownership

`PromptTemplateDiscovery.LoadAsync` consumes ordered, already resolved absolute
`PromptTemplatePathSelection` values. The caller must authorize these reads and
provide provenance and a qualified frontmatter decoder. Provenance does not
grant authorization. No environment, settings, conventional project directory,
package or user-home location is consulted. Relative paths fail before reads.

An explicit file is read only when its path has the ordinal `.md` suffix. An
explicit directory contributes direct `.md` file children in the file adapter's
enumeration order, without sorting or recursion. Hidden children are included:
this matches the pinned low-level explicit-directory loader, not the separate
automatic package/conventional-directory scanner and its ignore rules. Links
are statted following their target; file links are eligible, broken or directory
links are skipped. The default adapter uses .NET file APIs; custom adapters retain
the same boundary and can expose caller-owned file operations.

Every read is followed immediately by parsing before advancing to the next file.
Raw UTF-8 bytes preserve the BOM for the existing parser's exactly-one-BOM
removal. Read/parse failures and selected-root stat failures are warnings;
directory enumeration failures are silent. First-name-wins deduplication uses
the existing ordinal catalog helper. Read/parse warnings precede collisions.
Repeated paths are deliberately not canonicalized or deduplicated here.
`ReportMissingPath` opts a missing selection into the outer resource-loader's
error message, appended after collisions only if no diagnostic already has that
exact path; repeated missing selections produce one error. Absent ordinary selections are silent.

Caller cancellation is checked before discovery, between operations, after the
original read and before publishing a snapshot. An in-flight read is awaited
directly, without a cancellation race or detached task. Adapter and decoder
cancellation exceptions propagate unchanged. Cancellation never returns a
successful partial catalog. The caller owns lifecycle and resource generations.

`PromptTemplateResourceSet.LoadAsync` captures the resulting catalog and native
command metadata (name, description, argument hint, source, provenance). Its
`CreateInputAdmission` feeds the captured templates into the existing admission
pipeline: raw extension command boundary, raw input handlers, then expansion,
according to the explicit operation. A returned admission retains that capture
even after files change. No reload, session, queue or generation ownership moves
into this class. Reload owners replace their entire capture at their own boundary.

## Shared integration seam

The startup/resource owner supplies the actual resolved ordered selections,
project-read authority and decoder, then retains one `PromptTemplateResourceSet`.
The CLI dispatcher owner selects Prompt, Steer, FollowUp or ExtensionMessage
admission at the existing operation boundary. Pass raw input handlers separately
from any combined command admission, avoiding a second command dispatch. The
existing admission performs command-first handling and rejects known commands
for explicit queues. Extension messages default to expansion off. Templates
produce expanded user text before existing message materialization; they do not
start generation or commit queues.

If skill expansion is present, its owner must place it after raw input handlers
and before template expansion, as pinned `agent-session.ts` does. This patch does
not introduce skill loading. The command-list owner appends mapped prompt rows
after extension commands and before skills; editor completion owners consume
argument hints separately from exact RPC wire fields. Shared CLI/RPC dispatch,
test-runner registration and completion files are intentionally left to their
owners to avoid overlapping edits.

## Qualification and tests

Thirteen `PromptTemplateDiscoveryTests.Cases()` groups and three
`PromptTemplateResourceSetTests.Cases()` groups are authored in the existing
console-test convention. Register both collections in the shared coding-agent
runner. Filters are `prompt template discovery` and `prompt template resource
set`; also rerun the existing 32 parser/catalog/admission cases after the internal
catalog-helper extraction. These tests were not built or executed in this slice.

Synthetic metadata decoders test invocation order and diagnostics, not YAML
parity. No native qualified YAML parser exists in the base; full frontmatter
support remains dependent on the decoder owner. Plain templates and empty
frontmatter bypass that decoder. Default path resolution, package scanning,
ignore rules, project trust, symlink canonicalization, reload generations,
command wire mapping and live session integration remain owner work.

Links follow their targets, with no containment check or read-size budget in this
bounded loader. The .NET adapter's actual Windows symlink/reparse-point behavior, filesystem
enumeration order and malformed UTF-8 replacement equivalence to Node are
unqualified. Synthetic links and ordinary temporary-directory operations are
covered by authored tests; no privileged link creation is requested. Adapter
exception wording is native and is not claimed identical to Node.

Source evidence: pinned [prompt loading](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/prompt-templates.ts)
for file/direct-directory and explicit-selection loops; [resource loading](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/resource-loader.ts)
for warning/deduplication/missing-path ordering; [session input](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts)
for command/input/skill/template ordering; and [package scanning](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/package-manager.ts)
for separate automatic-directory filtering. This is not full CURRENT
resource-loader or live CLI acceptance.
