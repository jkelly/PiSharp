# Native bounded read/write tools

`PiSharp.Tools.Files.ReadWriteTools` supplies real framework-only .NET 10 UTF-8 file tools. The assembly references `PiSharp.Agent` and `PiSharp.Contracts`, with no Node runtime or package dependency. This is a first functional P3-05 slice; full built-in tool compatibility, images, other encodings, edit/shell tools and phase acceptance remain unfinished.

The host supplies an explicit absolute working directory, explicit absolute home directory and an `IToolActionPolicy`. There is no implicit allow policy. The supported composition is:

```csharp
var files = new ReadWriteTools(workingDirectory, homeDirectory);
var invoker = files.CreateInvoker(hostPolicy);
var toolDefinitions = files.CreateDefinitions(invoker);
var providerDeclarations = files.Declarations;
var systemToolsAdded = files.ToolsAdded;
```

`Adapters` is an immutable array of the prepared `read` and `write` adapters. `Declarations` contains owned `JsonData` objects with `name`, `description` and `parameters`; `ToolsAdded` is an owned JSON array of those declarations. These values can be passed to the provider/system declaration composition without borrowing a disposable JSON document. `CreateDefinitions` returns scheduler `ToolDefinition` values using the supplied invoker. The provider still has to translate declarations through its supported tool-declaration API. Direct adapter calls are available to trusted hosts as required by `IPreparedToolAdapter`; adapter validation supplies no authorization. Hosts must route effectful calls through the policy invoker, or provide an independently explicit trusted authorization boundary.

Public read arguments are `path` (required string), `offset` (optional positive 32-bit integer, default 1) and `limit` (optional nonnegative 32-bit integer). Public write arguments are required strings `path` and `content`. Unknown properties are rejected. Numeric fractional, exponent and decimal spellings that `JsonElement.TryGetInt32` cannot represent as integers are outside this profile. Empty write content and NUL in inert write-content values are supported; empty paths, malformed UTF-16 and NUL in paths/display strings are rejected. Actual resolved action target/cwd/argv/environment checks remain strict. There is no public `displayPath` parameter.

## Prepared final action and policy

Preparation validates arguments and performs read-only filename probes and canonical-path metadata resolution. It reads no file content and creates no directory or file. Read-only preparation probes therefore precede policy. The prepared immutable action has the tool name and operation `read` or `write`, kind `Path`, empty command arguments, the fixed working directory and an empty immutable environment. Its target is an absolute resolved path. Owned normalized arguments contain that identical path, `displayPath` holding the submitted display spelling, explicit read offset/optional limit, or write content.

After action transforms, validation requires `Target == Arguments.path`, an already normalized absolute target, the correct operation and the fixed action shape. Changing just the target is invalid. A trusted transform can change both target and normalized path and can replace write content; the mandatory policy sees the complete final action, and the executor uses that same immutable action's target and owned arguments. `displayPath` contributes only to model-facing notices. Execution repeats structural validation and never rederives its target from the original public call or display spelling. Queue canonicalization computes a coordination key and does not replace the authorized execution target.

The host policy must examine final target and operation and any relevant content. An arbitrary adapter or injected host callback can execute arbitrary native code; these interfaces are not a sandbox. Path identities can change between metadata resolution, policy and file opening. Canonical preparation and exact string handoff do not supply hostile path-race confinement or a handle-based filesystem authority model.

## Path and identity profile

`PathResolver` uses explicit cwd/home state. It replaces the upstream selected Unicode spaces with ASCII space, strips one leading `@`, expands `~` and `~/` (also `~\` on Windows), supports `file://` absolute URLs and resolves relative paths against the fixed cwd. Windows `/c`, `/mnt/c` and `/cygdrive/c` forms are converted to drive paths using the source pattern. It does not expand environment variables or `~otheruser`, trim whitespace or read ambient home variables. Windows drive-relative paths such as `C:child` are rejected because they depend on ambient drive state. Native file URLs with query/fragment components are rejected rather than implementing every Node URL edge case. URI/Windows extended-path and network-filesystem edge cases are not qualified as source parity.

Read filename probes follow source order: original resolved path, space-before-`AM.`/`PM.` replaced by U+202F, Unicode NFD, curly apostrophe substitution, then NFD plus curly apostrophe. The first existing variant is used; otherwise the original path is retained. Probes select metadata before file content is read. The default local existence probe treats I/O or access failures as absent; cancellation is checked around probes.

`LocalFileOperations.CanonicalizeAsync` resolves actual existing path segments through framework `LinkTarget`/`ResolveLinkTarget`, including supported directory symlinks/junctions and file symlinks. A missing segment or non-directory parent falls back to the entire normalized lexical path, corresponding to the selected source's missing-path fallback relationship. Other errors propagate. Existing alias resolution uses actual filesystem metadata; authored alias tests record whether symbolic-link creation was supported on the test platform. The resolver has a bounded recursive parent-resolution profile in addition to framework link-resolution behavior; it is not a universal realpath implementation.

The default queue key uses this real existing-segment resolver and folds Windows keys with invariant uppercase after resolution. This conservatively groups case aliases; Windows case-sensitive directories can be over-serialized. This does not discover hard links, preserve identity through path replacement or unify all volume/network aliases. A missing child under an aliased parent retains the full lexical fallback and can have a different key from another spelling of the same eventual destination. There is no actual alias-parity claim for such missing destinations.

Every `ReadWriteTools` instance owns one shared `FileMutationQueue` by default. Hosts composing several mutation adapters or instances must inject the same trusted queue to coordinate their effects. A supplied queue is responsible for its own canonical-key resolver and caps. External writers and other queue instances remain independent.

## UTF-8 reads and model-facing output

Local reads use an asynchronous `FileStream`, bound collected bytes while reading, and settle stream disposal before returning. The adapter rechecks injected returned memory against the input cap and makes its own copy. Strict UTF-8 decoding rejects malformed byte sequences, including legacy encodings that are not valid UTF-8. UTF-8 BOM text and CRLF bytes are preserved. NUL, decoded control characters other than TAB/LF/CR, UTF-16 BOMs and selected PNG/JPEG/GIF/WEBP/BMP signatures receive an explicit `UnsupportedContent` error. This is a conservative text admission profile, not complete binary-format identification: arbitrary printable valid UTF-8 bytes are interpreted as text. Images are not silently returned as image-compatible text; image decoding, resizing, original image metadata and model image blocks remain unfinished.

LF separates source lines, including a trailing empty file line. Offset is 1-based, limit is applied first, and selected content passes through the accepted shared `ToolOutputTruncator.Head` with 2,000 lines and 51,200 UTF-8 bytes. Returned lines retain their original content without added line-number prefixes. Selection and truncation notices carry the 1-based range and continuation offset. Empty files and limit zero are supported. An offset beyond the file's LF line array returns the source-shaped offset error. A first line exceeding the output byte budget returns the source-shaped `sed`/`head` suggestion as text; the tool does not run that command.

Truncated results have `details.truncation` with the shared source-shaped eleven fields: `content`, `truncated`, `truncatedBy`, `totalLines`, `totalBytes`, `outputLines`, `outputBytes`, `lastLinePartial`, `firstLineExceedsLimit`, `maxLines` and `maxBytes`. Those totals describe selected input to the truncator; the continuation notice describes the whole file. In particular, source truncator totals exclude a trailing LF's empty line while the read continuation count includes it. Successful untruncated results have native `JsonData.Null` details, representing upstream's absent details. The native result type always owns details.

## Writes, cancellation and cleanup

Writes strictly encode content to BOM-free UTF-8 bytes, enter the shared queue, create parent directories recursively and overwrite the exact final target using `FileMode.Create`. A BOM explicitly present in content is encoded as content. Empty strings truncate the file to zero bytes. There is no atomic temporary-file replacement, rollback, durability/fsync promise or conflict detection.

The queue is awaited through the admitted callback's completion, including directory/write callbacks and asynchronous `FileStream` disposal. Cancellation is checked before admission, while queued and between mkdir and write; the supplied token reaches file reads/writes. A canceled waiter retains its queue position behind active cleanup and cannot let a successor overtake its predecessor. An operation or injected callback that ignores cancellation can delay completion; there is no forced timeout or detached background effect. The trusted `IFileOperations` seam must settle all owned I/O and cleanup before returning or throwing and must not start hidden detached effects.

Admitted write cancellation and failure return `details.fileOperation` with a stable `code` and four booleans: `directoryAttempted`, `directoryCompleted`, `writeAttempted`, `writeCompleted`. Attempted means the callback was entered; completed means it returned successfully. A failing mkdir can have created some directories; a canceled/failing overwrite can truncate or partially modify the file. The flags do not promise absence, rollback, persistence or the exact bytes after a fault. If the write callback returns successfully after cancellation, `writeCompleted` remains true and the result reports cancellation rather than claiming no effect. The outer invoker preserves the returned result details when adding its cancellation diagnostic. Cancellation before an admitted callback need not have this write-effect record because no mkdir/write was attempted by this adapter.

Success returns `Successfully wrote to <displayPath>`. Host exception text is replaced by fixed native messages. Read errors, resource/content rejection and admitted write failures carry explicit native failure kinds/details rather than reproducing arbitrary upstream exception strings. Queue/admission/preparation failures pass through the invoker's stage failure handling. A failed admitted write releases its reservation only after settlement, so later same-key work can proceed.

## Bounds and evidence

Default immutable `ReadWriteToolOptions` caps are 8 MiB read input, 64 KiB encoded write content, 512 Ki UTF-16 argument characters and 4,096 UTF-16 path characters. Read/write byte caps may be configured from 1 to 64 MiB; argument characters from 1 to 8 Mi; path characters from 1 to 65,536. Public and normalized arguments must both fit the argument cap, so canonical/display paths and JSON escaping can reduce usable content below the byte cap. These are logical admission/read limits, not an exact CLR heap cap. Whole-file text splitting, owned bytes, escaped JSON and callbacks have bounded but additional allocations.

`CreateInvoker` passes the configured argument cap, an action cap accounting for paths/cwd and a 512 Ki-character result cap that accommodates source-shaped truncation text plus owned duplicated/escaped metadata. The queue defaults to 128 registered operations/keys and the configured path-character cap. Its snapshot is exposed read-only. These queue limits are checked before mutation callbacks; read-only preparation/key metadata can already have run. Reads are bounded individually and are not serialized with writes; no coherent concurrent file snapshot is promised.

Eleven authored native groups exercise real temporary filesystem effects, Unicode/BOM/CRLF/empty/shorter overwrites, read selection and output budgets, input limits/unsupported content, denial and invalid final actions, exact transformed target/content execution, path variants, cancellation before and during writes, canceled queue waiters, delayed cleanup with unrelated-key progress, late completed writes, fault recovery, and actual existing directory symbolic-link aliases when supported. Gates and Tasks control ordering; no sleeps or live provider/network calls are used. The test executable's report marks these as authored native evidence and records alias capability separately. Source/tests are supplied for root-owned isolated build and execution; no unexecuted pass or full phase acceptance is claimed here.

## Source relationship

The inspected clean public source is Pi v0.99.1 commit `d86654abb8862e201933517d6f1fce9f88dd117f`. Selected source SHA-256 pins are:

| Path under `packages/coding-agent/src` | SHA-256 |
| --- | --- |
| `core/tools/read.ts` | `297d26092979651091f44de8862a6fd55aaf59e7252f8fb8dd3d7cb3f9771518` |
| `core/tools/write.ts` | `2ee0a438c24cf83fefd733e349d55a21aa5b633c18934b2c80759fcf14b6f297` |
| `core/tools/path-utils.ts` | `ab6f420d2388a41366113c17a25e356867820b87b02a169d044cfcf83e1997d3` |
| `utils/paths.ts` | `64c3ebef724fa21ed0042e127908b4323a5d2f1b8aaa91eee550442332fe8502` |
| `utils/mime.ts` | `561d5511a976cdf4fc2d1b2ac773b21820bbad62e954e39c473f77c46a77f0af` |

Source reads/writes depend on Node filesystem/path built-ins, TypeBox, agent/AI extension types, tool-definition wrappers, renderers, image processing and MIME helpers, and the selected shared queue/truncator. The relevant unchanged sources were inspected; this slice executes native framework code and supplies no unchanged upstream read/write differential oracle. Model-facing text/selection expectations are authored from source inspection. The shared pure truncator has separate pinned captured evidence; that does not establish differential parity for real filesystem effects or full read/write behavior. Strict UTF-8/control admission, integer schemas, per-instance queues, cooperative cancellation, fixed errors and resource bounds are deliberate native profile choices. Retain the upstream MIT notice in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) when integrating these source-adapted tools.
