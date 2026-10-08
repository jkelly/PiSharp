# Pure prompt-template parsing and expansion

This source-only CURRENT slice ports the text behavior of Pi v0.99.1 at
`d86654abb8862e201933517d6f1fce9f88dd117f`. Base PiSharp candidate:
`75c18ddda73ce7d82954d701a582511d06a63dd1`. Twelve test groups are authored,
unregistered and unexecuted. No build, reference execution or independent
qualification is claimed. All phase/source/release gates remain open.

## Source identity and attribution

The supplied semantic-oracle manifest `.pisharp-semantic-api-oracle.json`
records the baseline and canonical file hashes. Each consulted file under the
oracle's `upstream` directory matched the corresponding hash exactly.

| Pinned source path | SHA-256 |
|---|---|
| `packages/coding-agent/src/core/prompt-templates.ts` | `11db52a6bea6f3f094c6f533d4a3162462bdef4693f238ba1a8a1be6729d4545` |
| `packages/coding-agent/src/utils/frontmatter.ts` | `99142d78b94e658e0be65cf05046be8069b3700168a683e0af64219ad903bcd6` |
| `packages/coding-agent/src/utils/text.ts` | `bb323f9607c499115b532021f4f0163c6df04d834aee9c32c3c77b273bd2d7c6` |
| `packages/coding-agent/test/prompt-templates.test.ts` | `4e6a5d427a78abe7c5ed92f71600f8777fddfd0f1f0d3cb1eb0a7e8655ecd2ee` |
| `packages/coding-agent/test/frontmatter.test.ts` | `88309e13d75cec874a1fa43b921c4bb3dbcbce55c6ace5acf9742e81667ad2dd` |
| `packages/coding-agent/docs/prompt-templates.md` | `ad5113cce0d05ef0ac77b3c71f9ebe41ea68c072275f9273b9616e4924feda42` |

The native algorithms are adaptations of this MIT-licensed Pi source,
Copyright (c) 2025 Mario Zechner. Applicable license and attribution are retained
in the existing `THIRD-PARTY-NOTICES.md`. There is no newer baseline adoption.

## Native API and format boundary

`PiSharp.CodingAgent.Resources` contains `PromptTemplate`, completion metadata,
an extracted document, `PromptTemplateParser` and `PromptTemplateExpander`.
Inputs are caller-supplied strings and lists. No file, environment, provider,
session, terminal or command registry is accessed. No authoritative transcript,
generation or process/cancellation owner is introduced.

`ExtractDocument(content)` removes exactly one initial BOM and normalizes CRLF
and lone CR to LF. It follows the source's literal opening `---` prefix and
first `\n---` search from offset 3, rather than imposing stricter Markdown
delimiters. Without a terminator the normalized text is the body. With one, the
body is trimmed using ECMAScript whitespace; a plain body retains its whitespace.
Empty extracted YAML bypasses decoding; whitespace/comment-only YAML does not.

`Parse(fileName, content, decodeFrontmatter)` takes the leaf filename already
chosen by its discovery owner and removes only an ordinal `.md` suffix. It uses
a string-valued description if nonempty, otherwise the first nonempty body line,
untrimmed, sliced at 60 UTF-16 code units with `...` if longer. A nonempty string
argument hint is retained. Unknown/non-string YAML values are the decoder's
responsibility; they must not become completion strings.

**Native YAML decoding is not implemented.** Pi calls its `yaml` package;
this slice requires an injected decoder returning the selected metadata. No
default parser silently interprets YAML, and no subset parser is advertised as
equivalent. The original decoder exception propagates from `Parse`; file-load
warning diagnostics and their exception handling belong to the future discovery
owner. Test decoder values are synthetic and establish only this boundary, not
YAML parsing or YAML parity. SourceInfo, absolute file path resolution,
directory/symlink handling, package discovery, trust, reload, completion and
actual slash-command routing remain outside this slice.

## Arguments and substitutions

`ParseCommandArgs` splits on the source ECMAScript whitespace set and accepts
single/double quotes, including adjacent quoted and unquoted fragments. Empty
quoted values vanish; quoted spaces survive; unmatched quotes consume the rest
without an error. Backslashes have no escape semantics. CLR-only whitespace
such as U+0085 does not split an argument.

`SubstituteArgs` runs one replacement pass over the original content:

- `$1`, `$2`, etc. select positions; `$0` or missing positions produce empty text.
- `$@` and `$ARGUMENTS` join every argument with one space.
- `${N:-default}`, `${@:-default}` and `${ARGUMENTS:-default}` use the default
  only for absent/empty values. Whitespace is a value.
- `${@:N}` and `${@:N:L}` slice from position N; zero starts at the first
  argument, zero length is empty, and out-of-range slices are empty.

Digit grammar is ASCII and case-sensitive. Leading zeros are accepted. Very
large values are saturated relative to the list size to preserve observable
array selection without numeric overflow. No placeholder boundary is added:
`$ARGUMENTSS` replaces the `$ARGUMENTS` prefix, and a backslash before `$1`
survives while `$1` is still substituted. Argument/default replacement values
are literal and never recursively expanded. No shell interpolation or execution
is performed.

`ExpandPromptTemplate(text, templates)` requires a leading slash, matches the
name ordinally, and selects the first matching template in the supplied list.
No match returns the original text. Whitespace, including newlines, separates
the name from arguments. It parses the remainder and substitutes only that
template's content. For example, a template `review` with content
`Review $1; ${@:2}` expands `/review "API compatibility" concurrency` to
`Review API compatibility; concurrency`.

## Tests and owner handoff

`PromptTemplateTests.Cases()` uses the existing CodingAgent console-runner
convention: `(string Name, Func<Task> Run)[]`. Twelve authored groups cover
quotes/empty arguments/literal backslashes, ECMAScript whitespace, positional
and wildcard values, nonrecursive defaults, slices, huge/ASCII digits,
first-match slash invocation, BOM/newlines/delimiter edge cases, metadata
fallback/UTF-16 truncation and decoder call/error ownership. These tests are
not registered automatically and have not been compiled or executed.

The existing runner owner may later append
`.Concat(PromptTemplateTests.Cases())` to `tests/PiSharp.CodingAgent.Tests/Program.cs`.
That file, all project files, CLI composition/runners, settings, skills,
extensions and providers are untouched here. Native build/test execution belongs
to the execution coordinator. Live CLI integration and
resource precedence remain separately owned. This slice enables a concrete
template expansion contract but does not make prompt files available in the CLI.
