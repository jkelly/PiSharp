# I-MERMAID implementation and evidence

Status: implementation integrated; isolated native core build, all differential comparisons, and all four full CLI Mermaid test groups passed in the root's serialized validation. The repository-wide stable rerun remains root-owned. Requested worker service tier ultrafast, effective tier unverified (delegation API exposes no tier selector).

## Implemented

- Native `Interactive/Mode/LovelyMermaid/` ports every source module of exact lovely-mermaid 0.3.3 used by Pi v1.5.2: nine diagram kinds, graph/compound and sequence layout, statements and labels, class styles and CSS named colors, role/class/href spans, frontmatter, source boxes, bounded lenient parsing and warnings.
- Existing `Interactive/Mode/Mermaid/` Grok implementation, 433 authored input corpus and original capture remain unchanged. The previous transformer tests now render through the active Lovely API while the historical Grok differential remains separate.
- `Components/MermaidTransformer.cs` admits only user/assistant/summary; preserves mode/width/top-level/warning fallback; uses exact role theme and author styles; permits OSC8 only on text spans with terminal hyperlink capability.
- Exact packaged Apache license copied to `third-party/lovely-mermaid.LICENSE.txt`, notice entry and CLI license output added.

## Frozen source and tests

`tools/LovelyMermaid/source-0.3.3.json` verifies official archive SRI and SHA-256, all packaged source/dist hashes, identity and license. Reference extraction is ignored under `artifacts/LovelyMermaid/reference-0.3.3/package`; no npm install, package scripts or lifecycle code executed. Actual reference captures execute only the reviewed distributed JS renderer.

New `lovely-0.3.3-corpus.json` contains 541 authored inputs (all 433 original input cases plus 108 new inputs). `lovely-0.3.3-goldens.json` contains actual Node execution of that exact package: diagram kind, 470 rendered arts and 71 null results; all roles/classes/hrefs/warnings and class definitions; selected default/custom ANSI and source boxes. Additional 163 direct style probes test all 148 CSS named colors plus class precedence, bold/fallback, invalid colors, every role and raw OSC8. The capture script validates every packaged file's hash before import.

New native `LovelyMermaidCases` auto-discovers without modifying shared Program registration. It compares the captured JSON structurally and string content ordinally, then verifies all six Markdown surfaces, mode/width fallback, author styles and text-only hyperlinks. No output is fabricated from native results or historical captures relabeled.

Commands already run successfully:

```text
node tools/LovelyMermaid/build-corpus.mjs
node tools/LovelyMermaid/capture-0.3.3.mjs artifacts/LovelyMermaid/reference-0.3.3/package tests/PiSharp.InteractiveParity.Tests/Mermaid/lovely-0.3.3-corpus.json tests/PiSharp.InteractiveParity.Tests/Mermaid/lovely-0.3.3-goldens.json
node tools/LovelyMermaid/gen-unicode-data.mjs artifacts/LovelyMermaid/reference-0.3.3/package src/PiSharp.Cli/Interactive/Mode/LovelyMermaid/MermaidUnicodeData.g.cs
node tools/LovelyMermaid/verify-width-data.mjs artifacts/LovelyMermaid/reference-0.3.3/package src/PiSharp.Cli/Interactive/Mode/LovelyMermaid/MermaidUnicodeData.g.cs
git diff --check -- [owned paths]
```

New Unicode tables match the capture runtime Node 24.22.0 / ICU 78.3 / Unicode 17.0. All 986 width-data runs equal the exact package. The original Grok Unicode profile remains intact.

The root's `validation` worker ran `dotnet run --project artifacts/LovelyMermaid/NativeProbe/NativeProbe.csproj`: compilation had zero warnings/errors; all 541 diagram comparisons plus 163 direct style probes passed, exit 0. The first validation wrapper receipt rejected its bare `PASS` line as insufficient selected-case evidence, although the assertions all ran. The case-labelled rerun passed with a stable receipt `sync152-mermaid-probe-r2-8362b316df9042479d9bdd00a1362324`.

The root's full InteractiveParity discovery run selected 750 tests and passed all four Mermaid groups: historical Grok differential, existing top-level/mode transformer, Lovely 541-diagram/163-style differential, and six-surface/style/hyperlink transformer. Receipt: `artifacts/gap-validation/sync152-interactiveparity-discovery-r2-77569f8c9bd84347ac81e2894953f564/receipt.json`. Unrelated source changed during this repository-wide run, so the final stable full rerun remains the root's responsibility; no Mermaid failures or further edits were identified. No native worker independently launched a build.

## Files and ownership

- M-CORE: `MermaidCanvas`, `MermaidGraph`, `MermaidStatements`, `MermaidLabels`, `MermaidSourceBox`.
- M-LAYOUT: `MermaidLayout`.
- M-GRAPH: `MermaidFlowchart`, `MermaidClass`, `MermaidEr`.
- M-STATESEQ: `MermaidState`, `MermaidSequence`, `MermaidLayoutSeq`.
- M-NEW: `MermaidPie`, `MermaidMindmap`, `MermaidTimeline`, `MermaidGitgraph`.
- M-I: `LovelyMermaid`, `MermaidCssColors`, new Unicode profile and shared JS width helpers; transformer; capture/generator tools; tests/captures; license/notice and CLI license packaging.

Only root may run dotnet during integration. An ignored, dependency-free native core test probe is prepared at `artifacts/LovelyMermaid/NativeProbe/NativeProbe.csproj` if unrelated CLI errors block the full suite. It reuses the same differential comparison and exact corpus, omitting only UI transformer tests.
