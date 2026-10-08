# Trusted source frame producer contract

This producer implements the exact factory API released by the session lead in
`TERMINAL_SOURCE_VIEW_RENDERER_OWNERSHIP_DECISION_R1.json`. The lead retains actual
CLI adoption, reset/startup signals and all aggregate/physical qualification.
The five production paths are the four released rendering files and minimal
internal window/border linkage in `TerminalEditorVisualMap.cs`. The independently
reviewed RGI width implementation is unchanged.

Pass the same captured, sealed SourcePolicyId map used for navigation, the view's
source scroll offset, an initialized immutable array of untrusted transcript
rows, focus and existing limits. The factory returns an immutable control-free
frame, source scroll, editor origin within that frame and total source rows.
It bottom-aligns the component in the actual viewport; newest transcript rows
occupy the remaining space above it. Empty transcript slots are blank rows.
The map's existing source window selection is shared with the exact component
projector. No keys are replayed, no second editor is created, and no alternative
word-wrap/navigation implementation is introduced.

Only internal typed inverse spans and anchor metadata authorize terminal effects.
They come from sealed source offsets and suffix segmentation, including scalar
seams inside combining/ZWJ/keycap sequences and registered marker suffixes.
Source inverse-caret appearance remains when unfocused; the hardware cursor is
hidden and only a focused, fully representable caret supplies its IME anchor.
The renderer emits fixed SGR0/7, ED/EL, bounded CUP and cursor visibility commands.
It emits no Pi APC, OSC, user controls, data newlines or tabs. Styles and anchor
eligibility participate in cache comparisons, and existing serial admission,
awaited physical completion, invalidation and joined disposal are retained.

Joe approved the narrowly documented safety differences: user ESC/C0/C1/DEL are
literal `\uXXXX` display data, forged markers cannot choose the caret, and tiny
windows cannot force invalid positions or reproduce recursive crashes. Recalled
HT expands to three spaces; normal editor admission still uses its original
four-space normalization. LF remains the source logical-row boundary. Thai/Lao
AM uses the source terminal decomposition. Untrusted transcripts use the existing
ASCII escape convention, including doubled backslashes. Original source expected
objects and unsafe wire observations remain unchanged; the separate safety
expectations file records these differences explicitly.

Safe control expansion consumes displayed cells before padding and caret
placement. The authoritative source row membership/navigation remains unchanged.
If its safe display exceeds the row, the factory clips at a whole grapheme,
reports `IsClipped`, and hides/unanchors a caret whose full selection cannot fit.
This is a declared safety difference, not source transport parity. Draft, source
map and scroll are preserved; rebuilding at a larger geometry recovers display.
Source focus-preserving crop handles heights1/2/3. A wide indivisible grapheme at
source content widths1/2 still fails in the existing pure map builder before this
factory can receive a map. The lead must preserve that draft and recover its
actual view on resize; this producer does not manufacture an invalid map.

No resource limit is expanded. Viewports, total source/transcript input,
visible frame text, grapheme size and exact full encoded output (including style
and movement overhead) are admitted before a factory frame can be written.
Pass a bounded transcript slice; the complete supplied transcript input is
included in the existing input budget even when older rows are outside the frame.
Unstyled TerminalTextLayout behavior and continuation-cell rejection remain
unchanged.

The producer probe retains all63 source observations. Benign normalized rows,
source focus crop and actual source TuiAltScreen anchor are compared with explicit
native transcript-origin translation. Unsafe cases retain both full source
expected output and safe native frame/writes. The 40 integration cases retain
all745 checkpoints and all66 factory presentations; their fixture host is not
the actual CLI. The original18/178 and additional8/80 default-preview HOLDs
remain open until actual default integration and independent review. No original
package or phase gate is accepted by these focused results.
