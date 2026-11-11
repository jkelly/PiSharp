# lovely-mermaid 0.3.3 reference and differential evidence

The CLI now uses the native C# port at `src/PiSharp.Cli/Interactive/Mode/LovelyMermaid`. The prior Grok port and its 433-case golden capture remain historical evidence. The files named `lovely-0.3.3-*` contain separately captured outputs from the actual exact npm package, not renamed native results.

`source-0.3.3.json` records the official locked archive, verified SHA-512 SRI, SHA-256 and every packaged file's SHA-256. Download the exact URL listed there and extract it without installing dependencies or running package scripts. The ignored local reference used during development is `artifacts/LovelyMermaid/reference-0.3.3/package`.

```powershell
node tools/LovelyMermaid/build-corpus.mjs
node tools/LovelyMermaid/capture-0.3.3.mjs artifacts/LovelyMermaid/reference-0.3.3/package tests/PiSharp.InteractiveParity.Tests/Mermaid/lovely-0.3.3-corpus.json tests/PiSharp.InteractiveParity.Tests/Mermaid/lovely-0.3.3-goldens.json
node tools/LovelyMermaid/verify-width-data.mjs artifacts/LovelyMermaid/reference-0.3.3/package src/PiSharp.Cli/Interactive/Mode/LovelyMermaid/MermaidUnicodeData.g.cs
```

The capture script validates every packaged file against the receipt before executing `dist/index.js`. The corpus has 541 authored inputs (433 original inputs plus 108 new inputs). Golden rows include kind, plain art, styled spans including optional classes/hrefs, class definitions, warnings, and for selected rows default/custom ANSI and source boxes. Additional direct style probes cover every CSS named color, role fallback, class order, unsupported styles, truecolor and OSC8. The file records the actual Node/ICU/Unicode runtime.

The new native Unicode tables were generated using the same Node v24.22.0 / ICU 78.3 / Unicode 17.0 runtime as the capture. `gen-unicode-data.mjs` preserves the prior port's probing algorithm; the prior Grok table remains unchanged. All 986 width-table runs match the exact 0.3.3 package.

`LovelyMermaidCases` is discovered automatically by the existing InteractiveParity harness. Its direct-source comparison is separate from that harness's overall authored-test report label. The transformer tests also verify only user/assistant/summary are admitted, author styles survive, only text spans receive links, and terminal capability gates those links.
