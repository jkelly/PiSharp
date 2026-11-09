# TUI generated-data analyzer diagnosis

Root approved a narrow annotation diagnosis after the normal Release analyzer gate timed out at 600 seconds. I13 confirmed the job was empty and the compiler/parent processes had exited before this source edit.

Only two declarations in `src/PiSharp.Tui/Input/TerminalTextEditorPasteController.cs` gained `[System.CodeDom.Compiler.GeneratedCode("PiSharp.WordData", "ICU78.3")]`: `PinnedWordDictionaryData` and `PinnedWordSeaData`. These contain generated Base64 data, byte/hash constants, and script range data; runtime algorithms and their analyzers remain unchanged. No global/file analyzer suppression was added. Historical generated source snapshots, input data, licenses and manifests were not edited.

Byte proof (also in `artifacts/gap-validation/tui-generated-code-annotation-proof.json`):

- Before: 3,723,924 bytes, SHA-256 `63407650ff0ed5a59e9990069bc0ea90adc242ca5390464d7bba895b3152c4fe`.
- After: 3,724,074 bytes, SHA-256 `8a7463ee1ba332d0e711e8141779f60549bf11550f8c3fa3ad6e8a765e657706`.
- Removing the two exact inserted attribute lines reproduces the original bytes and SHA-256 exactly. Git diff is two insertions, zero deletions; whitespace checks pass.

Before evidence: `Tui-Release-analyzer-gate-a3d34cf95a144045bb8e9ad5619d95a6` ended at `2026-11-08T23:43:05.6742062Z`, build timed out at 600 seconds. Earlier analyzer-disabled compilation passed, which is diagnostic evidence only.

After validation: I13's **normal analyzer** Release rebuild passed in 6.17 seconds, with zero warnings and zero errors. Receipt: `artifacts/gap-validation/Tui-Release-analyzer-generated-data-3eff1292ffd44a5b8ab90de3b1daf7a2/receipt.json`; flags include `-t:Rebuild`, `RunAnalyzers=true`, `RunAnalyzersDuringBuild=true`, and `ReportAnalyzer=true`. I13 reports a stable before/after source fingerprint. This bounded comparison supports the generated-data analyzer diagnosis: 600-second timeout before, 6.17-second full analyzer pass after the two annotations. Root owns any current derivative inventory refresh; historical pins remain unchanged.
