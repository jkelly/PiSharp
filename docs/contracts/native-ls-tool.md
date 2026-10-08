# Bounded native ls and initial search-tool inventory

Base: `4b63271a13e10505ab0e648592021050155454d1` from current local main, in a separate successor. This is a source implementation and authored-test inventory, not executed parity or a complete original search-tool inventory.

## Pinned source mapping

Pi v0.99.1: `d86654abb8862e201933517d6f1fce9f88dd117f`. Read-only detached oracle: `P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-reference-oracle-v0.99.1/upstream`. No upstream execution, download or trust-setting change.

| Source file | SHA256 | Relevant source behavior |
| --- | --- | --- |
| [ls.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/ls.ts) | `3d0a5457e4f5b9e9967c44608b76b060374d047e80d6871ecec4459b612cb6a3` | Optional string path, optional numeric limit/default 500; cwd fallback; existence/root stat/readdir; stable case-insensitive locale sort; child stat failures skipped; directory slash suffix; entry cap before stat; empty output before notices; whole-line 50 KiB head truncation; optional details. |
| [find.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/find.ts) | `b06bcae6821a0e9fda1b63be613a7ce28eb0e66e1d01d16564e59e83f9ce10ea` | Required glob pattern, optional path/limit/default 1000; custom glob seam or fd; hidden entries, git-aware ignore boundaries/no-require-git outside repositories; path-containing glob rewriting and Windows separator adaptation; relative POSIX paths; no-files result and result/byte notices. |
| [grep.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/grep.ts) | `88be3d00217d1a1caf8a9e7bb2b8d2e6d96edfa4b790c2cf76c746ab36b8841b` | Required pattern; path/glob/ignoreCase/literal/context/limit; default 100 matches with minimum one; rg JSON events, hidden files and gitignore; file/line match and context formatting; 500-character line truncation, whole-line byte cap; stop-on-match-limit, no-matches and process error paths. |
| [truncate.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/truncate.ts) | `8e4507c3ed7ca7548cf7c2d7f77d07f2ae63a38b756789cbb44e9062db6c8762` | 51200 UTF-8 bytes; complete-line head selection; original content and full truncation metadata; source search tools disable separate line cap using Number.MAX_SAFE_INTEGER. |

The base PiSharp.Tools source tree contains read/write, edit and Bash/process adapters and no grep/find/ls implementation or registration. The upstream index exports all three search factories; find/grep need additional admitted process semantics, so ls is the independent filesystem-only slice. This inventory covers these implementations and direct helpers, not every renderer, extension loading, CLI selection or upstream test.

## Implemented slice and boundaries

LsTool exposes its declaration, prepared adapter, mandatory-policy invoker factory and ToolDefinition factory. Optional empty/omitted path becomes dot using existing explicit cwd/home PathResolver. Optional finite numeric limit defaults to 500; fractional limits retain source comparison behavior (1.5 admits two successful entries), zero/negative limits yield the source empty-directory response, and an exact exhausted count produces no entry-limit notice. Failed child stats do not consume the returned-entry budget.

Preparation resolves and canonicalizes the target. Transforms pass through existing final-action revalidation and policy; target and normalized argument path must match, cwd/environment/command fields remain constrained, and execution revalidates the canonical identity. Policy sees the identical immutable final action and its transformed limit. No listing/stat execution occurs on denial. The directory action grants no bypass of host workspace policy and does not hard-code a new allowed root.

IDirectoryFileOperations extends the existing admitted IFileOperations seam; LocalFileOperations implements it using framework metadata and synchronous bounded enumeration. The enumerator's using scope disposes its OS handle on success, failure and cancellation. Async trusted seam calls are awaited directly without detached work or cancellation races; cancellation cannot settle the tool before the original host call and cleanup settle. No process runner, executable, package or dependency is introduced.

All entry names are checked before child traversal. Child canonicalization cannot escape the admitted directory; escaping links are skipped without target stat. This is a deliberate native boundary difference from Pi, whose fs.stat follows links. The target's existing canonicalization profile and possible concurrent filesystem replacement are unchanged; no atomic filesystem snapshot or native sandbox is claimed.

Listing includes dotfiles and adds slash for directories. Stable lowercase invariant-culture ordering matches the authored ASCII/case controls; equivalence to JavaScript's environment-dependent localeCompare for every Unicode name is NOT claimed. Input names retain Unicode text, but existing PathResolver bounds/scalar checks apply. Enumeration is bounded to 100000 names and 1 MiB total name characters; requested positive limit is capped at 100000, argument text at 16384 characters and paths at existing 4096 characters. Native bound failures reject rather than silently returning a partial sorted inventory. Exotic scientific numeric notice formatting is not source-qualified.

The existing ToolOutputTruncator supplies head selection at 51200 bytes with no practical line limit in this bounded inventory. Source-shaped details emit maxLines=9007199254740991, entryLimitReached and all eleven truncation members only when applicable. Ordinary/empty results omit details entirely. Notices retain source ordering and punctuation. Root missing/non-directory and directory-read error text follows source labels, with native ToolFailureKind.ExecutionError; generic unexpected failures/cancellation use the existing invoker classification. OS exception wording and abort rejection text are native rather than JavaScript-identical.

## Authored controls and execution status

Seven deterministic groups in LsToolTests are registered by one appended Cases concatenation in the existing Tools test runner. They cover real owned temp listing and declarations; stable case ties and numeric/default/exact/zero/negative limits; failed child stats and root/read errors; whole-line byte metadata and combined notices; exact transformed final policy and denial; malformed names/escaping fake identities/resource bounds; and a held original directory call that must finish cleanup before canceled settlement. No process launch or out-of-root file operation is authored. The cancellation test releases and awaits the original in finally before disposing its owned fixture root.

Builds/tests/SDK/upstream execution here: zero. Compiler qualification, independent review and coordinator-controlled runtime remain pending. Existing read/write/edit/Bash method bodies, all provider/terminal/CLI files, projects, package locks and held source pins are unchanged. The test runner append is the sole shared registration edit and must be composed with concurrent owners; it adds seven in-process groups and no runner process.

## Remaining gaps

- ls: production CLI/session installation and active-tool selection; dynamic source ctx.cwd parity; all-Unicode/default-locale ordering; unrestricted resource/numeric profiles; link-following parity across workspace boundaries; OS-specific error text, cancellation shape and actual platform differential execution; renderer integration.
- find: no native adapter yet. Pattern validation, fd admission/availability without download, owned original child/stdout/stderr/cleanup joins, ignore/repository boundaries, glob/path normalization, ordering and output/error/limit/cancellation controls remain.
- grep: no native adapter yet. rg admission/availability without download, owned JSON-stream process and cleanup joins, regex/literal/glob/case matching, context reads, match ordering, sanitized line/context formatting, all truncation notices and process/abort errors remain.

No original search-tool milestone, package or phase gate is closed by this slice.

## Test ownership review correction

The source reviewer reported production `0fe11c10152a3dc73750a0e2cbc0821ec14e596e` statically clear within its documented limits, but held merging for test ownership. The original Tools runner's outer 20-second WaitAsync could abandon the new cancellation group, and its entry-only wait could remain pending if the original invocation settled before entering the fake callback. Those findings are source review, not runtime observations.

This minimal successor directly awaits all seven LsToolTests method groups in the runner; other groups retain their original runner behavior. Cancellation installs finally before starting the original invocation, observes Task.WhenAny of callback entry and original settlement, and fails explicitly on early settlement. All paths cancel, release and await the original before disposing cancellation state or the owned temporary directory. Nested finally still releases and joins if cancellation itself throws. The normal pending-original and completed-cleanup/canceled-result assertions remain unchanged.

Complete seven-group source inspection found no other background tasks, wrapper waits or unjoined operations: the six other groups directly await each Invoke/file write and scope fixture disposal around those awaits. The cancellation callback's one held release wait is released by the owning test's finally and joined through the original invocation. No timeout is replaced by a detached observer; external coordinator process supervision remains separate.

No production, policy, ordering, result/error/limit assertion, source pin or original CLI/parity gap changes. Seven groups remain authored; zero builds/tests/SDK/upstream executions here. Independent correction review and immutable coordinator runtime qualification remain pending.
