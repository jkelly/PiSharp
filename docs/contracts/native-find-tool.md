# Bounded native find with an admitted custom-glob executor

Base: `5ab319781d3bca5d46ac938cc23e57ebe43874a6`, preserving the cleared ls core/ownership chain. This successor deliberately excludes pending ls CLI installation and independent provider work. No default tool activation, CLI policy or native namespace mapping changes.

## Pinned source and selected path

Pi v0.99.1 is pinned at `d86654abb8862e201933517d6f1fce9f88dd117f`. Inspected [find.ts at that immutable pin](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/find.ts), SHA256 `b06bcae6821a0e9fda1b63be613a7ce28eb0e66e1d01d16564e59e83f9ce10ea`, from the read-only detached local oracle. No upstream execution or downloads occurred.

Source parameters are required string pattern, optional string path and optional numeric limit/default 1000. Empty/omitted path resolves against cwd. Source has two distinct implementations:

- The custom FindOperations.glob branch checks root existence, calls glob(pattern, root, {ignore:["**/node_modules/**","**/.git/**"],limit}), retains returned order and duplicates, relativizes absolute paths, normalizes native separators and retains trailing directory separators. Empty output is `No files found matching pattern` with absent details. At count >= limit it reports the short `N results limit reached` notice; this path has no doubled-limit recommendation.
- The default path acquires fd, uses --glob/--color=never/--hidden, determines repository boundaries and --no-require-git, sets --max-results, rewrites path-containing glob patterns and Windows separators, streams lines and handles process spawn/exit/stderr. Its limit notice adds `Use limit=N*2 for more, or refine pattern`. Those default-fd semantics are NOT implemented or silently substituted here.

Both use source [truncate.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/truncate.ts) (SHA256 `8e4507c3ed7ca7548cf7c2d7f77d07f2ae63a38b756789cbb44e9062db6c8762`) for complete-line 51200-byte head output, no practical separate line cap, limit notice then byte notice, and optional resultLimitReached/truncation details.

This slice implements the source custom-glob adapter contract. It does not contain a production glob engine, fd path selector, binary discovery, download, fallback or process launcher. The existing admitted IProcessRunner was inspected: it exposes bounded original Windows process/pipe/output cleanup results, but there is no configured admitted fd executor/binary in this slice. No availability check or native launch was attempted. A future fd executor must be admitted and qualified separately.

## Supported domain and policy

FindTool requires a host-supplied IFindExecutor. Its SupportsPattern reports the executor's actual implemented glob domain; unknown syntax is rejected both before preparation and after transforms. The adapter never rewrites, approximates or matches glob syntax. FindExecutionRequest retains the exact pattern, final canonical search root, original fixed ignore filters and limit; executor-specific glob and ignore semantics remain its explicit contract. A host must not report unsupported syntax as supported or label a fixed-filter executor as a gitignore implementation.

Native argument domain: valid scalar nonempty pattern up to 1024 characters, existing explicit cwd/home/path profile up to 4096 characters, bounded object text up to 16384 characters, positive integral limits from 1 through 10000 (default 1000). The schema retains type number but declares its native bounds/integral profile. Pi's arbitrary fractional/nonpositive/unrestricted numeric profiles are not approximated; they are explicitly rejected. Only required pattern and optional path/limit are admitted.

FindTool exposes its declaration, prepared adapter, mandatory-policy invoker and ToolDefinition factory. Existing PathResolver/IFileOperations canonicalization prepares the final root. Existing ToolInvoker validates transforms, supplies the exact immutable final Path action to host policy, and refuses execution on denial. The adapter constrains tool/operation/root/arguments/cwd/command/environment shape and canonical identity using the reviewed ls pattern. Host workspace grants remain authoritative; no host policy is embedded or expanded and no tool is installed by default.

The admitted executor must honor the exact root, pattern, filters and result limit, preserve its original order, and return a settled immutable result. It owns and joins every original enumeration/process/stdout/stderr/callback/cleanup operation before returning or throwing, including cancellation. The adapter awaits the original call directly and checks cancellation after settlement. Executor final disposal remains with its borrowing host after all invocation joins. No timeout wrapper is introduced.

Returned paths are validated before any result is published: initialized array, count <= requested limit/10000, at most 1 MiB total original path characters, valid scalars and existing per-path bound. Lexical escape is rejected before canonical probing; canonical link escape is also rejected. Unlike source custom operations, escaping results or over-limit host receipts fail the whole operation rather than exposing ../ paths or silently capping partial results. Relative spelling/order/duplicates are retained within this safe domain; absolute names become relative native paths and separators become POSIX, including slash suffix. No sort, deduplication or unadvertised glob post-filter occurs.

The existing ToolOutputTruncator supplies source-shaped whole-line output and eleven-member truncation details; maxLines is emitted as source Number.MAX_SAFE_INTEGER. Details are absent on ordinary/empty output. Missing root and executor IO errors become native ExecutionError with source-shaped messages; unexpected exception/cancellation classification remains the native invoker's contract. Byte/UTF-16/scalar/resource bounds and cancellation settlement are native limits, not unrestricted JavaScript rejection-shape parity.

## Authored controls

Seven directly awaited Tools groups:

1. TempGlob: a test-only explicitly admitted executor supports exactly *.txt and *.md over its owned clean fixture tree. It uses existing IDirectoryFileOperations to enumerate recursively and settle handles, implements ordinal suffix glob matching and fixed .git/node_modules filtering, rejects links/ignore-rule files/deep trees and preserves its deterministic descending DFS order. This is an authored custom-executor domain, not an fd/gitignore implementation. Requires hidden file inclusion, nested matches, exclusion, exact source filter/root/default limit forwarding, required pattern/numeric schema and policy-bound definition.
2. Unsupported: initial and transformed path-glob/classes/braces/question/empty/NUL patterns and fractional/nonpositive/over-bound limits reject without executor calls. The finite fake executor advertises only its implemented two-pattern domain.
3. PathOrder: injected order, duplicates, absolute/relative path normalization and trailing directory slash remain unchanged.
4. Limits: empty absent-details result, exact-count short source custom notice and 51200-byte complete-line truncation/metadata/notice ordering.
5. Policy: exact transformed pattern/root/limit reach final policy and execution; denial has zero executor calls and target/argument mismatch is refused.
6. Errors: missing root, executor IO error, lexical/canonical escape, over-limit and uninitialized host receipts produce errors without published partial paths. Escaping fake identities perform no actual external filesystem access.
7. Cancellation: observes executor entry or early original settlement, installs finally before invocation, then cancels/releases/joins the original on every exit. The held callback's cleanup must finish before canceled tool settlement.

All seven use direct runner awaits, including the existing ls direct-await branch; unrelated runner behavior remains unchanged. All real fixture files are inside a verified unique temp root; tests await original operations before recursive cleanup. No external process or live provider call is authored. Tests/builds/SDK/upstream executed here: zero. Source/compile/runtime acceptance remains pending.

## Remaining gaps

- A concrete production admitted glob/fd executor and its published supported domain; this slice supplies the adapter and admission contract only.
- Real fd binary availability/admission, process exit/stdout/stderr/final-cleanup ownership, default-fd argument construction, exit/stderr/partial-output behavior and longer fd notice.
- General globs, gitignore/ignore-file rules and nested repository boundaries, Windows/native path-pattern adaptation, Unicode/case/platform differential evidence and renderers.
- Actual session/CLI registration and explicit tool selection after an executor is admitted. Installation is intentionally withheld rather than registering a tool with no usable executor; no default authority expansion.
- Broader source numeric/path/error/cancellation parity, unrestricted bounds and source differential execution.
- Grep remains unimplemented; existing ls platform/Unicode/resource differences remain open.

No original search milestone, package or phase gate closes.
