# I-CLI-TOOLS implementation

Reviewed baseline: sync-1.5.2-cli-discovery.md. Upstream source read at frozen 1.5.2 b4afdddf44e059c67ccf5097e5317d913521b470. Requested hard/high and ultrafast; effective service tier unverified.

## Implemented

- #9654: optional `IStreamingFileOperations` now accepts seekless readable streams. All integral offsets/nonnegative integral limits use one pass, preserving BOM and nonfatal UTF-8, counting all file lines and selected bytes while retaining only capped output. Local seekable files use the length captured at open, including prefix sniff bytes; zero-length/special streams run to EOF. Fractional/negative limits retain bounded buffer fallback. Images retain bounded binary reads. Cancellation/error paths dispose owned streams.
- #8544: edits map only changed normalized boundaries back into original content. Untouched mixed CRLF/LF/CR stays verbatim; replacement text uses the starting line's EOL (preceding EOL for unterminated last line), and CR/LF seams cannot silently merge line breaks. Fuzzy mapped ranges are retained for diff generation. `FormatEdit` computes 32-line-margin merged windows with original/global line numbers and bounded output. Both preview and execution use it; a failed diff cannot write.
- #9262: Windows path globs enumerate fd output, normalize input backslashes, apply a native dot-enabled/noext smart-case matcher, and expand trailing `/**` to `/**/*`. Limits count matching paths and retain only output through the byte cap plus the needed extra result. Ancestor lookup and immediately-before-spawn cancellation checks prevent a known-canceled call from starting fd.
- #8842: `ShellConfiguration` appends the upstream PowerShell 5.1 NativeCommandError correction suffix only for `powershell.exe`, preserving trailing-backtick commands and pwsh. Native BashTool validation validates the prepared suffix-bearing command.

## Existing implementations audited

Bash timeout/shell-exit cause survives native execution. ShellCommandExecutor already counts raw output bytes independently of retained text and spills from that count. PiGrepTool retains streaming context/UTF-16 and relative glob behavior. Find/ls limits normalize to at least one and require an extra matching result before announcing truncation. No changes to durable imported tools, MCP child process classes or historical goldens.

## Validation

Direct Windows PowerShell 5.1 process verification passed all six exact-suffix cases: redirected native stderr with exit 0, redirected native stderr with exit 7, parser error, cmdlet error, explicit exit 7, and successful output (observed exits 0/1/1/1/7/0).

No dotnet build launched by this worker; root owns serial build/test execution. Added `Sync152ToolsCases.Cases()` with nine `sync152.*` groups covering seekless ranges/decoder boundaries, huge-line bounds/snapshot append exclusion, stream cancellation/error cleanup, mixed EOL and fuzzy edit mapping, large-file diff windows and failure-before-write, Windows glob behavior and cancellation before spawn, PS51 suffix, and ls exact/normalized limits. Root to register this group and execute:

```
dotnet run --project tests/PiSharp.Tools.Tests -- --filter sync152
dotnet run --project tests/PiSharp.Tools.Tests -- --filter edit
dotnet run --project tests/PiSharp.Tools.Tests -- --filter read
dotnet run --project tests/PiSharp.Tools.Tests -- --filter "gap files"
```

Updated hand-authored existing test assertions for the new diff-failure behavior and buffer-only fractional/negative range fallback; historical golden assets untouched. `git diff --check -- src/PiSharp.Tools tests/PiSharp.Tools.Tests/Sync152ToolsCases.cs` passed (line-ending normalization warnings only).

## Owned paths

`src/PiSharp.Tools/Files/{ReadWriteTools,FileOperations,PrefixReadStream,EditPlan,EditMatcher,EditTool,EditPreview,DiffFormatter,PiFindTool,WindowsFindGlob,PiSearchProcess}.cs`; `src/PiSharp.Tools/Processes/{ShellDiscovery,BashTool}.cs`; `tests/PiSharp.Tools.Tests/{Sync152ToolsCases,EditToolTests,GapFileToolsTests}.cs`.

## Root validation follow-up

Root serialized full Tools run built successfully and passed all nine new sync152 groups (169/173 groups overall). Four failures identified an independent-preview-budget regression, large all-changed-line Myers trace exhaustion, and two intentional mixed-EOL expectation changes. Fixed preview to omit unused patch generation/budget; added a proven-disjoint changed-line fast path so bulk edits avoid quadratic trace. Updated hand-authored mixed-EOL assertions. Added `Sync152EditExpectations.cs`, an explicitly versioned 1.5.2 filesystem-byte overlay after the existing C05 overlay; original capture JSON and hashes remain unchanged, and differential evidence retains historical, C05, and 1.5.2 expectations. Root rerun pending; this worker did not launch a build.
