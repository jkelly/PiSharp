# Kitty alternate key fields — P5-08

This unit implements bounded CSI-u alternate fields in native C#. Its source and
consumer are uncompiled and unexecuted. Runtime qualification, independent exact
review, the combined native gate, the physical terminal matrix, and the original
package and phase gates remain pending. All 79 original scopes and eight OPEN
phase gates remain required.

## Source boundary

The reference is Pi v0.99.1 at
`d86654abb8862e201933517d6f1fce9f88dd117f`:

- [keys.ts](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/tui/src/keys.ts)
  supplies alternate grammar, binding matches, printable decoding, and actions.
- [editor.ts](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/tui/src/components/editor.ts)
  supplies default editor binding order and character jump behavior.
- [tui.ts](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/tui/src/tui.ts)
  filters releases before component dispatch unless the component opts in.

Source requests Kitty flags 7, including alternate-key flag 4. The captures use
unchanged public Source functions and the actual focused `TuiAltScreen` input
callback, with every imported source/dependency checked against the frozen
manifest. Node is an evidence tool; production uses no Node bridge or dependency.

The four original Source schedules and 12 complete checkpoints are retained in
`tests/PiSharp.Tui.KittyAlternate.Tests/fixtures/alternate-key-source-observations.json`.
The actual baseline at `8651358786c8362dd70253eb94f7adcb8436e9d2` had 18 mismatched
checkpoints out of 24 across the legacy and acknowledged routes. That baseline
remains immutable and was not rerun. No corrected runtime result is claimed.

## Admission and identities

The decoder admits `code[:shifted[:base]][;modifier[:event]]u` after CSI, including
an empty shifted field with a present base. Source also permits an event after a
complete alternate triple without the semicolon modifier field; the decoder
retains the same native action 1..3 restriction for that form. Primary and
supplied alternate numbers must fit the existing native integer and valid Rune
contract. Empty base, extra fields, overflow, unsupported modifier bits, and
third-semicolon associated text remain unknown packets with the raw text intact.

The allowed raw modifier mask remains 207. CapsLock and NumLock bits 64/128 are
removed from binding modifiers; they do not imply Shift. Press, repeat and release
remain event metadata. Sequence/chunk/paste limits, injected-clock deadlines,
incomplete-prefix behavior, UTF-16 validation and fail-closed lifetime are unchanged.

The public decoded `TerminalKey(Key, Modifiers, Action)` keeps its existing
primary canonical identity and its three-property shape. Alternate provenance
belongs to the original decoder event through a private weak table. Constructed
keys and external `with` clones acquire no provenance. Equality, hashes, record
formatting, JSON, constructors and deconstruction are unchanged.

Printable selection uses shifted only while Shift is present, otherwise primary.
It ignores base and accepts only Shift plus lock state. Thus `97:65;2u` inserts
`A`, while `1092::97;1u` inserts Cyrillic `ф`, despite Source `parseKey` reporting
Latin `a` for the latter. Pending character jumps use this printable identity.

Ordinary projection checks the pinned default editor binding order before
printable insertion. Source matching preserves normalized Latin-letter/symbol
authority and can also match base for an unrecognized primary. `119::97;5u`
therefore remains Ctrl+W. A Cyrillic primary with base 97 and Ctrl projects to
Ctrl+A. Source `parseKey` retains digit identity, while `matchesKey` can match a
digit's base as well: `49::97;5u` matches both Ctrl+1 and Ctrl+A, and the default
editor executes Ctrl+A. The public original record still reports primary `1`.

The private matcher keeps both possibilities until ordered default projection.
It uses the exact Source keypad equivalents and symbol set; it does not broaden
Unicode letter/symbol categories or normalize base independently. Keypad Left
57417 with base Backspace chooses deletion before cursor movement. With base `q`,
it moves Left rather than inserting `q`. Source treats 57350 differently from the
existing native single-scalar Left alias; alternate projection uses the pinned
Source table, and old single-scalar mappings remain unchanged.

Projection preserves modifiers and action and is idempotent through the existing
host/controller calls. Global Ctrl+C still projects before component dispatch.
The original keypad and modifyOtherKeys provenance branches remain unchanged.

The R31 static review found an omitted Shift+Space binding. Source handles it
after jump triggers and before printable insertion: `ESC[32:65;2u` inserts a
literal space during an ordinary edit, while its printable `A` remains the target
of an already pending jump. The successor adds that exact binding precedence.
If shifted is non-printable (`ESC[32:13;2u`), Source cancels a pending jump and
then inserts space. An internal decoder-created `Space` marker carries the same
private weak origin until ordinary projection, allowing the unchanged controller
to cancel before inserting. External marker clones acquire no origin. The public
original record and its modifiers/action remain unchanged.

## Explicit old-subset assertion supersession

The old contract in `native-terminal-input.md` deliberately excluded alternates.
Its authored control `unsupported-grammar-ESC[120:88;193u` expected Unknown and
passed at the old pinned product. That assertion and report remain preserved.

The R30 source-path release supersedes exactly this one control in the new unit.
Source parses and inserts `x`: mask 192 contains only locks, so shifted 88 is
unused. The successor control asserts one `TerminalKey("x", None, Press)` and
ordinary printable `x`; the new actual editor Source capture retains the whole
expected state object. The old control maps to
`source-backed-alternate-admission-ESC[120:88;193u`. No Source golden is rewritten,
and earlier bounded acceptance remains valid at its immutable products.

Every other old control remains. Action 0/4 rejection is a narrower native
policy; Source treats them as press. Source rejects extra modifier fields,
associated text, and the empty modifier form. These different categories are
recorded explicitly rather than described as identical Source rejections.

## Consumer and remaining criteria

The frozen consumer was authored before production edits. It retains all four
original schedules, ten supplemental Source schedules, and the reviewed `x`
schedule: the R31 15 schedules/35 complete Source checkpoints remain unchanged.
This successor adds 26 Shift+Space schedules/34 checkpoints, covering four lock
states, press/repeat/release, shifted printable/control values, and the two
pending-jump branches. A future permitted run must compare all 138 route
checkpoint objects across 41 schedules/69 Source checkpoints and join all 82
owned harnesses.
It stores each complete Source expected step in the report and does not seed
editor state, filter schedules, prune failed expectations, or waive mismatches.
Authored identity/split/prefix/deadline/bound/weak-origin controls are separate.

`fixtures/held-io-failure-cancellation-design.json` specifies eight further
cleanup schedules: held read and write crossed with failure and cancellation on
both host routes. It defines explicit entry/release/cancellation/settlement gates,
retained original tasks, failure identity, zero borrowed disposals, joined I/O
counters and causal evidence. The immutable design remains labelled planned at
its original boundary. `HeldIoTests.cs` now implements these eight designs in a
separate harness; `fixtures/held-io-source-boundary.json` identifies the source
pins. This successor has not been compiled or executed. Independent execution
remains mandatory and supplies no cleanup or behavior acceptance now.

The read gate travels with the alternate packet and holds its following physical
read, after admission. The completed draft/frame is observed through the existing
same-value focus barrier. The write gate is armed only after startup paint and
holds the next actual view write. Its draft and full attempted write are retained;
the view observation precedes physical output and supplies no visible-frame
comparison while the write is held. Cancellation registration records the token
without settling either noncooperative operation. Explicit releases either return
EOF/finish the write or throw one unique IOException instance.

Receipts retain the exact original run, run observer, physical I/O, paint, focus
and disposal tasks and an ordered causal log. The close task awaits the original
run before starting view disposal; a held checkpoint labels disposal not started,
never joined. Watchdog deadlines only diagnose a failed control barrier. Cleanup
releases owned gates and still awaits the original close, including its original
tasks. Each receipt preserves the primary failure and separate cleanup errors,
checks balanced I/O and zero borrowed disposal, and probes focus detachment and
the acknowledged end/receipt lifetime after the run joins. No sleeps or successful
EOF stand in for a cancellation/failure outcome. A hang cannot produce acceptance.

### Reviewed checkout admission and durable diagnostics

The source-only R35 successor addresses static review findings
`KITTY-HELD-EVIDENCE-01` and `KITTY-HELD-EVIDENCE-02`. The future runner requires
seven arguments, in order: original Source observations, fresh final report path,
executing reviewed repository root, immutable candidate source manifest path,
manifest SHA-256, permitted build receipt path, and build receipt SHA-256. These
arguments do not authorize execution; a fresh parent policy-permitted boundary
is still required. No build receipt has been created by this source-only work.

The manifest uses the sealed handoff format (`repository`, `candidate`, `tree`,
and `files` with `relative`, `path`, `bytes`, `sha256`). Every relative path is
resolved inside the explicit reviewed checkout and its physical bytes are
checked. Producer absolute paths are provenance only. The test boundary's pins
must agree with the immutable manifest, and copied output fixtures plus the
explicit original Source input must match it. Manifest and build receipt bytes
are hashed and parsed from the same read. Admission errors are persisted and
return failure before any replay or held-I/O scenario starts.

The future build receipt has `schemaVersion: 1`,
`policyPermittedNativeBoundary: true`, `candidate`, `tree`,
`reviewedRepositoryRoot`, `sourceManifestSha256`, and `dlls`. Each of the three
DLL entries (`PiSharp.Cli`, `PiSharp.Tui`, `PiSharp.CodingAgent.Tests`) supplies
`name`, `path`, `bytes`, `sha256`, and `informationalVersion`. They must match the
actual executing assemblies within the reviewed checkout. The independently
supplied receipt SHA pins this association to the permitted build; a claimed
permission field is evidence metadata and does not grant permission itself.

`<report>.held-io.jsonl` is created fresh and append-only. Each structured line
is flushed through the writer and file stream to disk. It records admission,
earlier Source evidence, each completed held-I/O row, and watchdog/control
failures before the final unbounded await of the original close task. A pending
join is explicitly `incompleteJoin: true`, `allOriginalTasksJoined: false`, and
`passingReceipt: false`. The diagnostics contain source/DLL pins, injected and
observed failures, original task identities/states, counters, cleanup errors and
causal events. Close/recovery faults are persisted before subsequent ownership
joins. A hang leaves durable failure evidence while the harness continues to
own its original tasks. It cannot advance to the next scenario while a join or
diagnostic persistence is incomplete. Final reports include the sealed journal
identity. None of these source changes has been compiled or executed.

The following criteria remain OPEN:

- Compilation, actual corrected route results, immutable DLL identity and
  independent exact review. No native build/run is authorized by the source release.
- Actual release validation. Source's focused TUI filters releases. Both native
  host loops already require a non-release `TerminalKey` before editor dispatch,
  and the reader's global interrupt check also excludes release. The direct
  controller handles word commands before its own release check; that separate
  accepted behavior is retained. Source-only inspection establishes the existing
  host filter, but the complete release schedule still requires a permitted run.
- Custom bindings, autocomplete/select widgets, viewport interception, and
  untested alternate named-key/Source helper disagreements. Default projection
  does not establish those owners' parity or alter their shared APIs.
- Invalid or unused alternate numbers where Source's permissive JavaScript
  helpers differ from native integer/Rune/UTF-16 admission. Source observations
  remain intact; native safeguards are not relaxed to produce an equality claim.
- Accepted keypad/Kitty/MOK/focus/default/Indic/word/RGI/paste-marker/resource and
  lifetime regression contracts, including all 34 MOK interrupt rows and two
  held reads, followed by the lead's full native and physical gates.

The exact R24 `PiSharp.Tools.dll` App Control HOLD remains distinct. This source
unit supplies no retry, rebuilding, repackaging, relocation or alternate execution
route for that blocked product and cannot substitute its missing combined gate.
