# Contributing to PiSharp

PiSharp is in early implementation. Use the eight phase plans as the execution source and record implemented behavior separately from proposed requirements. Useful contributions include source-linked compatibility fixtures, small native slices and review of the remaining phase boundaries.

1. Open an issue describing the proposed change and its rationale before substantial work.
2. Keep documentation pull requests focused. Distinguish planned behavior from verified observations and implemented functionality.
3. Cite public upstream sources with immutable commit links when describing baseline behavior.
4. Submit only material you have the right to share publicly. Exclude credentials, personal data, confidential material, and private repository content.
5. Preserve applicable copyright and license notices for any future third-party material. Contributions are provided under this repository's MIT license.

Run `.\tools\test-native.ps1` from the repository root with the SDK pinned in `global.json`. The framework-only native suite uses offline fixtures and no provider keys. For documentation changes, check links, spelling, and consistency with the [planning index](docs/plans/README.md). Do not claim tests passed or parity was achieved without corresponding implementation and evidence.

Keep [implementation status](IMPLEMENTATION_STATUS.md) and the machine-readable task state current. Authored fixture expectations, captured upstream results, developer checks and independent acceptance are distinct evidence kinds. Never regenerate goldens automatically or silently retarget the pinned upstream commit.
