# FILE1 find-limit release blocker

Status: coherent fixture correction ready for main's focused .NET validation. No production source changes, builds, .NET execution, Git operations or remote operations performed. Requested model/tier: sol high, Ultrafast; effective tier is unverified. Scope is `tests/PiSharp.Tools.Tests/GapFileToolsTests.cs` only.

## Evidence and diagnosis

- Hosted Core Tools job `128927363032`, run `42544805912`, fails `gap files rg context globs UTF16 and normalized limits` with `Exact find limit reported truncation.` Evidence: `artifacts/release-prep/core-42544805912/core-Tools/console.log:162`.
- That assertion runs **before** `Text(exact)`, which asserts `!IsError`. Consequently an error result's details are indistinguishable from truncation at this assertion. The fd resolver originally called `Executable("fd")` inside ToolInvoker execution; a missing executable throws there and the invoker converts exceptions to an execution error with details (`src/PiSharp.Agent/ToolInvoker.cs`). The old test never checks the actual returned text or success before failing.
- `.github/workflows/core.yml` originally installs only ripgrep. There is no fd path/version in the downloaded Core Tools artifacts. An unavailable fd is the concrete prerequisite hypothesis; the artifacts do **not** establish the hosted fd executable or version. CI2 owns explicit provisioning and provenance. Do not claim a reproduced fd version regression.
- Production `PiFindTool.Arguments` already passes `--max-results limit+1`, and `SearchAsync` marks `resultLimitReached` only for `lines.Count > effectiveLimit`. It retains the needed exact-versus-excess proof. No production correction is justified by the evidence.

## Changes

1. Resolve both native executable prerequisites at the beginning of the search fixture, outside ToolInvoker. Missing fd now reports `Offline fixture requires installed fd` directly.
2. For each exact normalized limit `0`, `-2`, `1`, `1.9`, require a successful result containing exactly `src/utf16.txt`, then require absent details.
3. Preserve the two-file excess cases (`0`, `-2`, `1.9`), assert success before details, assert numeric `resultLimitReached == 1`, and retain exactly one displayed result.

The existing ripgrep cwd/glob/context/UTF16, symlink-rooted glob and file cancellation checks remain intact. No assertions were removed or tool paths skipped. `FdFindExecutor` is unchanged.

## Local native-command verification

Resolved tools:

- `C:\Users\jksof\.kimi-code\bin\fd.exe`: `fd 10.4.2`.
- `C:\Users\jksof\AppData\Local\Microsoft\WinGet\Packages\BurntSushi.ripgrep.MSVC_Microsoft.Winget.Source_8wekyb3d8bbwe\ripgrep-15.1.0-x86_64-pc-windows-msvc\rg.exe`: `ripgrep 15.1.0 (rev af60c2de9d)`.

Created a workspace-local search fixture containing `src/utf16.txt` written using `Encoding.Unicode`, then added `other.txt`. Actual raw commands, with `$searchPath` set to `artifacts/release-prep/file1-fixture-verified/search`'s absolute path:

```powershell
fd --glob --color=never --hidden --no-require-git --max-results 2 -- '*.txt' $searchPath
# Before adding other.txt: one result, src/utf16.txt; exit 0.
# After adding other.txt: two results, other.txt and src/utf16.txt; exit 0.
# With cwd=$searchPath, matching PiGrepTool's normalized glob and cwd:
rg --no-config --json --line-number --color=never --hidden --glob 'src/*.txt' --context 1 -- 'needle' '.'
# exit 0; context line 1 before, match line 2 needle, context line 3 after.
```

Machine-readable command output and resolved executable versions: `artifacts/release-prep/file1-fixture-verified/raw-evidence.json`. The first exploratory rg command used unnormalized `./src/*.txt` against an absolute target and exited 1; the corrected command above matches production's normalization/cwd and succeeded. This was a raw command setup error, not a tool test result.

Main retains the .NET lease. Required next validation is the Tools suite filter `--filter "gap files"` (covers both registered gap-file groups), followed by main's broader Tools gate and exact-SHA hosted Core Tools rerun with CI2 prerequisite provenance. These have not been run by FILE1.
