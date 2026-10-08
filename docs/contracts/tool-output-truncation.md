# Pure tool output head and tail limits

`PiSharp.Agent.Tools.ToolOutputTruncator` supplies the shared string primitive needed by P3-05 and P3-07. It substantially adapts `truncateHead` and `truncateTail` from Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`, in `packages/coding-agent/src/core/tools/truncate.ts` (SHA-256 `8e4507c3ed7ca7548cf7c2d7f77d07f2ae63a38b756789cbb44e9062db6c8762`). The upstream MIT notice is retained in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).

The synchronous native API accepts a string and optional `ToolOutputTruncationOptions`. Defaults are 2,000 lines and 51,200 UTF-8 bytes. Explicit zero is a valid budget. Native budgets are nonnegative signed 32-bit integers; negative values throw `ArgumentOutOfRangeException`, including with empty input. Null content throws `ArgumentNullException`. Missing native options use defaults; the fixture adapter maps missing or JSON-null individual options to the upstream defaults. Fractional, negative, nonfinite and wider JavaScript number budgets are outside the matched domain.

`Head` retains complete lines from the beginning. If the first line alone exceeds the byte budget, it returns empty content with `FirstLineExceedsLimit = true`. `Tail` retains complete lines from the end. Only when its last source line cannot fit before any line is admitted does it retain a partial suffix, advancing to a UTF-8 character boundary. It does not admit a partial earlier line after a later line has fitted. No omission marker is appended.

LF is the only line separator. Empty content has zero lines. One final LF is removed for counting, so `"\n"` has one empty line and `"a\n\n"` has two lines. CR, Unicode line/paragraph separators, BOM and NUL remain text. An untruncated result preserves the exact source string, including a trailing LF. A truncated result joins the admitted lines with LF and removes the final LF.

The result carries every field from the selected upstream result shape. Native field and enum names use C# casing; this record is not a Pi wire serializer.

| Native field | Meaning |
| --- | --- |
| `Content` | Retained source text or decoded partial tail |
| `Truncated` | Whether the source's original line or byte totals exceeded a budget |
| `TruncatedBy` | `Lines`, `Bytes`, or null; source precedence is preserved |
| `TotalLines`, `TotalBytes` | Original LF-counted lines and UTF-8 byte count |
| `OutputLines`, `OutputBytes` | Source-compatible admitted-line count and UTF-8 byte count of `Content` |
| `LastLinePartial` | Source's flag for its partial tail edge case |
| `FirstLineExceedsLimit` | Head rejected its first line's byte length |
| `MaxLines`, `MaxBytes` | Budgets actually applied |

The source's metadata has several observable edge cases. A partial tail counts as one output line, including an empty suffix when no complete UTF-8 character fits. With a one-line budget, the final line-limit check changes `TruncatedBy` to `Lines` even when that partial tail was admitted because of bytes: tail of `"abcdef"` with limits `(1, 3)` is `"def"`, with `LastLinePartial = true` and `TruncatedBy = Lines`. If truncation is triggered solely by a final LF's byte, the result may retain every counted line and still report `Lines`: `"a\nb\n"` with a three-byte budget returns `"a\nb"`. These fields deliberately match the pinned source rather than an inferred interpretation of its comments.

Strings may contain unpaired UTF-16 surrogates. UTF-8 byte counting replaces each invalid code unit with U+FFFD, matching Node's `Buffer.byteLength` and `Buffer.from`. Unchanged content and retained complete lines preserve their original UTF-16 code units. A partial tail comes from UTF-8 bytes and therefore contains replacement characters where invalid source code units survive the cut. For example, `"\ud800x\udc00"` counts as seven bytes; its four-byte partial tail is `"x\ufffd"`. Cuts preserve Unicode scalar UTF-8 boundaries, and may split grapheme clusters such as a letter and its combining mark.

This API takes already decoded strings. It neither accepts nor classifies binary bytes and does not choose file encodings. NUL-containing text is accepted as text. External-byte decoding, binary/image detection and malformed external UTF-8 belong to a future file/process adapter. .NET string/array and `Encoding` size limits still apply; this primitive materializes input lines and does not supply a streaming-memory cap.

The authored corpus in `fixtures/pi-v0.99.1/tool-truncation/core.input.json` has 124 cases across 20 categories. It includes empty text; zero and exact budgets; default 2,000-line and 50-KiB boundaries; head/tail line order; oversized first/last lines; LF/CRLF/CR and trailing blanks; BOM/NUL; two-, three- and four-byte Unicode; combining characters; maximum native integer options; null/default options; and invalid UTF-16. Each case is passed to both unchanged upstream exports, producing 248 observed results. The input is synthetic PiSharp-authored test data, not an upstream test fixture. Expected results are genuine upstream observations.

`capture-tool-truncation.mjs` directly imports the unchanged source module using Node type stripping. Its dependency closure is that one import-free module and Node's built-in `Buffer`; there are no external packages, patched source files, extracted implementations or resolution shims. The runner pins the source commit, module bytes and exact qualified Node 24.19.0 executable, checks whole-source cleanliness before and after each capture, runs two independent observation processes and requires byte-identical traces. Git's ownership exception is scoped to each read-only command's exact source directory and never changes global configuration. Evidence writes use exclusive creation and refuse to overwrite existing outputs.

To recapture, create a fresh directory and run from the repository root in PowerShell:

```powershell
New-Item -ItemType Directory fixtures/pi-v0.99.1/tool-truncation/new-capture
& 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' --experimental-strip-types --disable-warning=ExperimentalWarning tools/PiReferenceRunner/capture-tool-truncation.mjs ../Pi-upstream-v0.99.1 fixtures/pi-v0.99.1/tool-truncation/core.input.json fixtures/pi-v0.99.1/tool-truncation/new-capture
```

The retained input hash is `c573c8413bfa1c78a872d17672d5f0a861ce2637b05055e6fa11c5f87f2f5c04`. Both raw captures and `core.expected.json` hash to `8c088a8230dcf627221c2b3d3f22b2c7dc4aee2f3bd9e8b434f3c13b2124496d`. Provenance hashes to `e2aca3bc5a26256f0d17a6c7c64bd65afdd68b2b24e035b46439d39043d0e00f`. Provenance also records source/runtime/runner hashes, the dependency closure, before/after source checks and capture sizes. No observation fields are normalized. JSON string tokens with escaped lone surrogates are decoded losslessly by the native test adapter because `JsonElement.GetString()` rejects such tokens; the adapter has an independent exact-code-unit check.

`ToolOutputTruncatorTests.FrozenReferenceCorpus` verifies the evidence pins and compares all eleven fields in all 248 results. `NativeInputPolicy` tests explicit native argument rejection and UTF-16 handling. Build and test execution is recorded by the root integration owner after selective freeze.

This primitive performs no filesystem, process, tool, environment, credential, network or provider actions. It is not yet connected to read/shell tools or output accumulators. `truncateLine`, `truncateMiddle`, `formatSize`, structured 1-MiB shell results, effectful tool behavior, policy/sandbox enforcement and cross-platform acceptance are outside this slice. P3-05/P3-07 and every phase gate remain open.
