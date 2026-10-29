---
title: Sessions library
description: Read, write and transform Pi session files with PiSharp.Sessions.
---

`PiSharp.Sessions` reads and writes Pi's session files: one JSONL file per session, version 3, holding a tree of entries. It lists sessions, walks their trees, builds the context a model sees for a branch, plans compactions and branch summaries, forks a branch into a new file, and copies or migrates files. It calls no model, runs no tools and depends only on `PiSharp.Contracts`.

The [coding agent](/docs/embed/coding-agent/) builds its persistent sessions on this library. Use `PiSharp.Sessions` directly when you want to inspect, index, convert or generate session files without running an agent. For what users see (resume, branch, fork, compact), read the [Sessions guide](/docs/guides/sessions/). For where the files live, see [Files and locations](/docs/reference/configuration/#sessions).

```powershell
dotnet add package PiSharp.Sessions --version 1.2.0.1
```

## Namespaces

| Namespace | Main types |
| --- | --- |
| `PiSharp.Sessions.Serialization` | `SessionEntry`, `SessionEntryCodec`, `SessionEntryMigration` |
| `PiSharp.Sessions.Storage` | `SessionLogReader`, `SessionLogStore`, `SessionStorageBackend`, `SessionBranchPublisher` |
| `PiSharp.Sessions.Tree` | `SessionTreeQueries`, `SessionTreeSnapshot`, `SessionBranchPlanner` |
| `PiSharp.Sessions.Context` | `SessionContextProjector`, `SessionHistoryProjector`, `SessionSystemReplay`, `SessionContextEditValidator` |
| `PiSharp.Sessions.Compaction` | `SessionCompactionPlanner`, `SessionBranchSummaryPlanner`, `SessionCompactionTokenEstimator`, `SessionSummaryRequestBuilder` |
| `PiSharp.Sessions.Lifecycle` | `SessionCatalog` |
| `PiSharp.Sessions.Import` | `SessionCopyService` |

Every operation has limits you can configure. Failures are typed exceptions with a `Failure` enum and a fixed message that never contains session text.

## The file format

A session file is UTF-8 JSONL without a byte order mark. The first line is the header. Every other line is one entry, and each entry names its parent, so the file holds a tree. Entries are only ever appended. When you branch, the next entry's parent is an older entry, and the file keeps both branches.

```text
{"type":"session","version":3,"id":"0199f2c4-6b7e-7c3a-9d1e-2f4a5b6c7d8e","timestamp":"2026-10-28T09:15:02.114Z","cwd":"/home/me/project"}
{"type":"model_change","id":"3f9a1c2e","parentId":null,"timestamp":"2026-10-28T09:15:02.120Z","provider":"anthropic","modelId":"claude-sonnet-4-5"}
{"type":"thinking_level_change","id":"7b2d4e6f","parentId":"3f9a1c2e","timestamp":"2026-10-28T09:15:02.121Z","thinkingLevel":"medium"}
{"type":"message","id":"c1d2e3f4","parentId":"7b2d4e6f","timestamp":"2026-10-28T09:15:10.003Z","message":{"role":"user","content":[{"type":"text","text":"List the tests"}],"timestamp":1793178910003}}
{"type":"message","id":"a5b6c7d8","parentId":"c1d2e3f4","timestamp":"2026-10-28T09:15:14.871Z","message":{"role":"assistant","content":[{"type":"text","text":"There are three test projects."}],"api":"anthropic-messages","provider":"anthropic","model":"claude-sonnet-4-5","usage":{"input":1204,"output":9,"cacheRead":0,"cacheWrite":0,"totalTokens":1213,"cost":{"input":0.0036,"output":0.000135,"cacheRead":0,"cacheWrite":0,"total":0.003735}},"stopReason":"stop","timestamp":1793178914871}}
```

This is the format Pi's session manager writes. Pi's [session format](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/session-format.md) page describes it from the TypeScript side. Pi and PiSharp read each other's files.

### Header

| Field | Type | Meaning |
| --- | --- | --- |
| `type` | `"session"` | Marks the header. |
| `version` | integer | Must be `3`. Older versions need [migration](#copy-migrate-and-archive). |
| `id` | string | The session id. Must not be empty. |
| `timestamp` | string | When the session was created, as an ISO 8601 string. |
| `cwd` | string | The working folder the session belongs to. |
| `parentSession` | string, optional | The path of the session file this one was forked from. |

PiSharp names new files `<timestamp>_<id>.jsonl`, with `:` and `.` in the timestamp replaced by `-`, as Pi does.

### Entries

Every entry has these fields:

| Field | Type | Meaning |
| --- | --- | --- |
| `type` | string | The entry kind. Must not be empty. |
| `id` | string | Unique within the file. Pi writes short random ids. |
| `parentId` | string or `null` | The parent entry, or `null` for a root. Must be present. |
| `timestamp` | string | When the entry was written, as an ISO 8601 string. |

The kinds, with the fields the codec requires and what each contributes to the model's context:

| `type` | `SessionEntryKind` | Required fields | Optional fields | In the context |
| --- | --- | --- | --- | --- |
| `message` | `Message` | `message` (see [message roles](#message-roles)) | | The message. |
| `thinking_level_change` | `ThinkingLevelChange` | `thinkingLevel` | | Sets the thinking level. |
| `model_change` | `ModelChange` | `provider`, `modelId` | | Sets the model. |
| `usage` | `Usage` | `kind`, `provider`, `model`, `usage` | `note` | Nothing. Counts in usage totals. The cache warmer writes `kind` `"cache_warm"`. |
| `compaction` | `Compaction` | `summary`, `firstKeptEntryId`, `tokensBefore` (integer, not negative) | `details`, `usage`, `fromHook`, `systemMessage` | Replaces the history before it with a summary. |
| `branch_summary` | `BranchSummary` | `fromId`, `summary` | `details`, `usage`, `fromHook` | A summary of the branch you left. |
| `custom` | `Custom` | `customType` | `data` | Nothing. Extension state. |
| `custom_message` | `CustomMessage` | `customType`, `display` (boolean), `content` | `details` | A `custom` message, sent to the model as a user message. |
| `context_edit` | `ContextEdit` | `targetId`, `replacement` (`null`, or an object with `content`) | | Replaces the content of an earlier entry, or drops it when `null`. |
| `label` | `Label` | `targetId` | `label` | Nothing. Labels the target in the tree. A missing, `null` or empty label clears it. |
| `session_info` | `SessionInfo` | | `name` | Nothing. The last one names the session. |
| anything else | `Unknown` | | | Nothing. Kept as inert data. |

A compaction's `details` and a branch summary's `details` hold `readFiles` and `modifiedFiles` arrays, which the next summary carries forward. `usage` has the shape described under [message roles](#message-roles).

### Message roles

A `message` entry's `message` object has a `role` and, for the known roles, an integer `timestamp` in Unix milliseconds:

| Role | Required fields | Sent to the model as |
| --- | --- | --- |
| `system` | `content` (string or text blocks) | A system message. Optional `sections` and `toolsAdded` hold the system prompt and tool changes. |
| `user` | `content` (string or text and image blocks) | A user message. |
| `assistant` | `content` (text, thinking and tool call blocks), `api`, `provider`, `model`, `usage`, `stopReason` | An assistant message. |
| `toolResult` | `toolCallId`, `toolName`, `isError`, `content` (text and image blocks) | A tool result. |
| `bashExecution` | `command`, `output`, `cancelled`, `truncated` | A user message saying what ran and what it printed, unless `excludeFromContext` is true. Optional `exitCode` and `fullOutputPath`. |
| `custom` | `customType`, `display`, `content` | A user message with that content. |
| `branchSummary` | `summary`, `fromId` (string or `null`) | A user message wrapping the summary. |
| `compactionSummary` | `summary`, `tokensBefore` | A user message wrapping the summary. |

Other roles are kept and never sent. `stopReason` is one of `stop`, `length`, `toolUse`, `error`, `aborted`, `deferred` or `pending`. `usage` is an object with the numbers `input`, `output`, `cacheRead`, `cacheWrite` and `totalTokens`, and a `cost` object with the numbers `input`, `output`, `cacheRead`, `cacheWrite` and `total`.

Content blocks are `text` (`text`), `image` (`data`, `mimeType`), `thinking` (`thinking`) and `toolCall` (`id`, `name`, an object `arguments`). Images are allowed in user, tool result and custom content; thinking and tool calls only in assistant content. Other block types are kept but never interpreted.

## Entries and the codec

`SessionEntry` is one validated record. It has no public constructor: you get one from `SessionEntryCodec`, a reader or a store.

| Member | Meaning |
| --- | --- |
| `Kind` | The `SessionEntryKind`, from `type`. |
| `Type` | The `type` string. |
| `Id`, `ParentId`, `Timestamp` | The common fields. `ParentId` is `null` for roots and for the header. |
| `IsHeader` | True for the `session` header. |
| `WireBody` | The whole record as `JsonData`. Read every other field from `WireBody.Value`, a `JsonElement`. |

There are no typed classes per entry kind. The raw JSON stays authoritative, including fields PiSharp doesn't know, so a file you read and write back keeps everything it had.

`SessionEntryCodec` parses and serializes one record:

| Member | Meaning |
| --- | --- |
| `SessionEntryCodec(SessionEntryCodecOptions? options = null)` | Creates a codec with these limits. |
| `CurrentVersion` | The constant `3`. |
| `Parse(string json)` | Parses and validates one record. |
| `ParseUtf8(ReadOnlySpan<byte> json)` | The same from UTF-8 bytes. |
| `Read(JsonElement value)` | The same from an element. Its raw text is parsed again strictly. |
| `Serialize(SessionEntry entry)` | Returns the record as one compact line without a line ending. Whitespace outside strings is removed; string escapes, number spelling, property order and unknown fields are kept. |

The codec checks the required fields in the tables above and nothing more. It accepts only strict JSON: no comments, no trailing commas, and no duplicate property names at any depth. Integers that must be integers (message timestamps, `tokensBefore`, a `bashExecution` exit code) must fit in a 64-bit signed integer, without a fraction or exponent. A header whose version is not 3 fails with `UnsupportedVersion`.

| `SessionEntryCodecOptions` | Default | Limit |
| --- | --- | --- |
| `MaximumRecordCharacters` | 1,048,576 | UTF-16 characters in one record. |
| `MaximumUtf8Bytes` | 4,194,304 | UTF-8 bytes in one record. |
| `MaximumJsonDepth` | 32 | Nesting depth, between 1 and 1,000. |

`SessionEntryCodecException.Failure` is one of `CharacterLimit`, `Utf8ByteLimit`, `DepthLimit`, `MalformedJson`, `InvalidRecord`, `UnsupportedVersion`, `DuplicateProperty`, `UnsupportedUnicode` and `UnsupportedInteger`.

## Read a file

`SessionLogReader` reads a whole file into memory and validates it line by line. It never changes the file. It returns the records it validated before the first problem, and says what the problem was:

```csharp
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// Pi's files can be larger than the defaults allow, for example when they hold images.
const int Limit = 64 * 1024 * 1024;
var reader = new SessionLogReader(new SessionLogReaderOptions(
    MaximumInputBytes: Limit, MaximumLineBytes: Limit,
    CodecOptions: new SessionEntryCodecOptions(Limit, Limit, JsonData.MaximumDepth)));

var log = await reader.ReadFileAsync(path);
foreach (var diagnostic in log.Diagnostics)
    Console.WriteLine($"line {diagnostic.LineNumber}: {diagnostic.Message}");

if (log.Header is { } header)
    Console.WriteLine($"session {header.Id} in {header.WireBody.Value.GetProperty("cwd").GetString()}");

ImmutableArray<SessionEntry> entries = [.. log.ValidatedPrefix.Skip(1).Select(record => record.Entry)];
foreach (var entry in entries)
    Console.WriteLine($"{entry.Id} <- {entry.ParentId ?? "root"}  {entry.Type}");
```

`ReadAsync(Stream source, bool leaveOpen = true, CancellationToken)` reads from a stream instead. The other APIs on this page take the entries without the header, as above.

`SessionLogReadResult`:

| Member | Meaning |
| --- | --- |
| `Status` | `Complete`, `RecoveryRequired` (a damaged line), `UnsupportedVersion` (a header that is not version 3) or `ResourceLimit`. |
| `ValidatedPrefix` | The valid records before the first problem, header first. Each `SessionLogRecord` has its `Entry`, `LineNumber`, `ByteOffset`, `ContentByteLength`, `PhysicalByteLength` and `HasLineTerminator`. |
| `Header` | The header, or `null` when there is none. |
| `Diagnostics` | What was found, each a `SessionLogDiagnostic` with a `Code`, position, `Message` and `IsBlocking`. |
| `ValidatedPrefixByteLength` | Bytes covered by the valid prefix. |
| `SourceComplete` | Whether the whole file was read. |
| `OriginalBytes` | The bytes read. |
| `OpaqueHeader`, `DetectedVersion` | The header's JSON and version, when its version is not 3. |

A valid last line without a line ending reports `FinalLineWithoutNewline`, which doesn't block. A last line cut off mid-write reports `IncompleteFinalJson` or `IncompleteFinalUtf8`. Other codes are `EmptyLog`, `MissingHeader`, `EmptyHeader`, `InvalidHeader`, `UnsupportedHeaderVersion`, `FutureHeaderVersion`, `UnexpectedHeader`, `InvalidRecord`, `InvalidUtf8`, `Utf8Bom`, `InputByteLimit`, `LineByteLimit`, `LineCountLimit`, `RecordCountLimit` and `CodecLimit`. Blank lines are skipped. A `SessionLogReadException` (`ReadFailed`, `CleanupFailed`) means the file itself could not be read.

| `SessionLogReaderOptions` | Default | Limit |
| --- | --- | --- |
| `MaximumInputBytes` | 16,777,216 | Bytes in the file. |
| `MaximumLineBytes` | 4,194,304 | Bytes in one line. |
| `MaximumLines` | 100,000 | Lines, including blank ones. |
| `MaximumRecords` | 100,000 | Records, including the header. |
| `ReadBufferBytes` | 8,192 | Read buffer, between 1 and 65,536. |
| `CodecOptions` | codec defaults | The limits for each record. |

:::note[Damaged lines]
Pi skips lines it can't parse when it loads a session. PiSharp's reader stops at the first one and reports it, so you can decide what to do, and a `SessionLogStore` won't open such a file for writing. The defaults are also tighter than Pi's. PiSharp's CLI raises them, up to 536,870,888 bytes, the longest string Node can hold and so the largest file Pi can read.
:::

## Walk the tree

`SessionTreeQueries.Build` validates the whole tree (unique ids, every parent present, no cycles) and returns an immutable `SessionTreeSnapshot`:

```csharp
using PiSharp.Sessions.Tree;

var tree = new SessionTreeQueries().Build(entries);
Console.WriteLine($"name: {tree.SessionName ?? "(none)"}, leaf: {tree.PhysicalLeafId}");

// Depth first, indenting only where the tree branches.
var pending = new Stack<(string Id, int Depth)>();
for (var index = tree.RootIds.Length - 1; index >= 0; index--) pending.Push((tree.RootIds[index], 0));
while (pending.Count > 0)
{
    var (id, depth) = pending.Pop();
    var node = tree.ById[id];
    var label = node.ResolvedLabel is { } resolved ? $" [{resolved.Label}]" : "";
    Console.WriteLine($"{new string(' ', depth * 2)}{node.Entry.Type} {id}{label}");
    var childDepth = node.ChildIds.Length > 1 ? depth + 1 : depth;
    for (var index = node.ChildIds.Length - 1; index >= 0; index--) pending.Push((node.ChildIds[index], childDepth));
}

// The branch that ends at the last entry, root first.
foreach (var entry in tree.GetLatestBranch())
    Console.WriteLine($"{entry.Timestamp} {entry.Type}");
```

Long sessions are deep chains, so walk the tree with a loop, not recursion.

| `SessionTreeSnapshot` member | Meaning |
| --- | --- |
| `Entries` | The entries in file order. |
| `ById` | Each entry's `SessionTreeNodeView`: its `Entry`, `ChildIds` in file order and `ResolvedLabel`. |
| `RootIds` | Entries whose parent is `null`. |
| `StructuralLeafIds` | Entries without children. |
| `BranchPointIds` | Entries with more than one child. |
| `PhysicalLeafId` | The last entry in the file, the leaf when the file is opened. |
| `Labels`, `LabelsAvailable` | Each labelled entry's `SessionResolvedLabel` (`Label`, `Timestamp`), from the last `label` entry that targets it. Unavailable when an entry's latest label has a value that is not a string. |
| `SessionName`, `SessionNameAvailable` | The trimmed `name` of the last `session_info` entry. |
| `MetadataDiagnostics` | Problems with labels and the name. |
| `GetEntry(id)` | The entry, or `null`. |
| `GetChildren(parentId)` | The children in file order. |
| `GetChronologicalChildren(parentId)` | The children sorted by timestamp, as Pi's tree view orders them. Returns `UnsupportedTimestamp` when a timestamp isn't ISO 8601. |
| `GetBranch(entryId)` | The path from the root to that entry. |
| `GetLatestBranch()` | `GetBranch(PhysicalLeafId)`. |
| `GetLabel(id)` | The label, or `null`. Throws `MetadataUnavailable` when labels are unavailable. |

`SessionTreeQueryOptions` takes `GraphOptions` (the [context limits](#build-the-models-context)), `MaximumQueryEntries` (100,000) and `MaximumMetadataDiagnostics` (256). `SessionTreeQueryOptions.For(contextOptions)` matches a session's own context limits. An invalid tree throws `SessionContextProjectionException`; a limit or missing entry throws `SessionTreeQueryException`.

## Build the model's context

`SessionContextProjector` turns a branch into the messages a model sees. `Project(entries, selectedLeafId)` uses the branch that ends at that entry, and `ProjectLatest(entries)` uses the last entry in the file:

```csharp
using PiSharp.Sessions.Context;

var context = new SessionContextProjector().ProjectLatest(entries);
Console.WriteLine($"model {context.Model?.Provider}/{context.Model?.ModelId}, thinking {context.ThinkingLevel}");
foreach (var message in context.LlmMessages)
    Console.WriteLine($"{message.Role}: {message.WireBody.ToString().Length} characters");
```

The projector follows Pi's rules:

1. It walks from the selected entry up to the root.
2. The thinking level is the last `thinking_level_change` on the branch, or `off`. The model is the last `model_change` or assistant message.
3. If the branch has a compaction, the context starts with the latest one, then the entries from its `firstKeptEntryId` up to it (without system messages), then everything after it.
4. Each `context_edit` on the branch replaces its target's content, or drops the target when the replacement is `null`.
5. Messages, custom messages, branch summaries and the compaction add messages, as the [kinds table](#entries) says. Other entries add nothing.

| `SessionContextProjection` member | Meaning |
| --- | --- |
| `LeafId` | The selected entry. |
| `Ancestry` | The branch, root first. |
| `ById`, `RootIds` | All entries by id, and the roots. |
| `Messages` | The context in its stored roles (`custom`, `branchSummary`, `bashExecution` and so on), each a `TranscriptEntry` with `Role` and `WireBody`. |
| `LlmMessages` | The same converted for a provider: only `system`, `user`, `assistant` and `toolResult`. |
| `ThinkingLevel` | The thinking level string. |
| `Model` | The `SessionContextModel` (`Provider`, `ModelId`), or `null`. |
| `ContextEntries` | Each contributing entry with the messages it added. |

`SessionContextProjector.AgentMessages(context)` keeps `custom` messages and converts the rest, which is what an agent loop holds before it builds a request. A projector caches its last result, so projecting again after appending one message is cheap.

| `SessionContextProjectionOptions` | Default | Limit |
| --- | --- | --- |
| `MaximumEntries` | 100,000 | Entries in the file. |
| `MaximumAncestorSteps` | 100,000 | Entries on the branch. |
| `MaximumInputCharacters` | 16,777,216 | Characters of all entries. |
| `MaximumOutputMessages` | 100,000 | Messages produced. |
| `MaximumOutputCharacters` | 16,777,216 | Characters of the messages produced. |

`SessionContextProjectionException.Failure` is `InvalidEntries`, `DuplicateId`, `MissingParent`, `Cycle`, `MissingLeaf`, `ResourceLimit`, `UnsupportedInfluence`, `UnsupportedMessage` or `UnsupportedTimestamp`.

Before you write a `context_edit`, `SessionContextEditValidator.Normalize(context, new SessionContextEditDraft(targetId, replacement))` checks that the target is on the branch and editable (a user, assistant or tool result message, or a custom message), and returns the replacement to store.

### History and usage

`SessionHistoryProjector` builds the history a user sees, which ignores compaction and edits, together with counts and usage totals:

```csharp
var history = new SessionHistoryProjector().ProjectLatest(entries);
var statistics = history.SessionStatistics;
Console.WriteLine($"{statistics.UserMessages} user, {statistics.AssistantMessages} assistant, {statistics.ToolCalls} tool calls");
if (statistics.Totals is { } totals)
    Console.WriteLine($"{totals.Total} tokens, ${totals.Cost:F4}");
```

| `SessionHistoryProjection` member | Meaning |
| --- | --- |
| `Tree`, `Context` | The tree snapshot and the model context. |
| `FullHistory`, `BranchHistory` | All entries, and the selected branch. |
| `HistoryMessages`, `DisplayMessages` | The branch's messages without compaction or edits, and the ones a transcript shows. |
| `SystemHistory`, `EffectiveSystemMessage`, `SystemState` | The system messages, and the current system prompt and tools from `SessionSystemReplay`. |
| `SessionStatistics`, `BranchStatistics` | Counts and usage for the whole file and for the branch. |

`SessionHistoryStatistics` has `UserMessages`, `AssistantMessages`, `ToolCalls`, `ToolResults`, `TotalMessages`, `Status` and `Totals` (`Input`, `Output`, `CacheRead`, `CacheWrite`, `Total`, `Cost`). Usage comes from assistant messages, `usage` entries, summaries and tool results that carry it. When one has a usage value it can't add up, `Status` is `UnsupportedUsage`, `Totals` is `null` and `UnsupportedRecordIndexes` says which.

## Write a session

`SessionLogStore` is the writer for one file. It holds the file open and appends entries in batches. With the default file storage, it flushes each batch to disk before it updates its snapshot:

```csharp
using System.Globalization;
using System.Text.Json;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

var codec = new SessionEntryCodec();
var now = DateTimeOffset.UtcNow;
var timestamp = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
var header = new SessionBranchPlanner().New(Guid.CreateVersion7().ToString(), timestamp, workingDirectory).Header;

await using var store = await SessionLogStore.CreateNewAsync(path, header);
var user = codec.Parse(JsonSerializer.Serialize(new
{
    type = "message", id = "a1b2c3d4", parentId = (string?)null, timestamp,
    message = new { role = "user", content = "Hello", timestamp = now.ToUnixTimeMilliseconds() },
}));
var name = codec.Parse(JsonSerializer.Serialize(new
{
    type = "session_info", id = "e5f6a7b8", parentId = user.Id, timestamp, name = "Greeting",
}));

var result = await store.AppendAsync([user, name]);
Console.WriteLine($"leaf {store.Snapshot.LeafId}, on disk: {result.CheckpointAcknowledged}");
```

`SessionBranchPlanner.New(sessionId, timestamp, workingDirectory, parentSession)` builds a valid header. You can also parse your own with the codec.

| Member | Meaning |
| --- | --- |
| `CreateNewAsync(path, header, options, cancellationToken)` | Creates the file, which must not exist, and writes the header. Cancellation is ignored once creation starts. |
| `OpenAsync(path, options, cancellationToken)` | Opens an existing file. It must read `Complete`, every id must be unique, and every parent must come earlier in the file. |
| `AppendAsync(entries, cancellationToken)` | Appends a batch. Appends run one at a time. |
| `Snapshot` | The committed `SessionLogStoreSnapshot`: `Header`, `Entries`, `ById`, `LeafId` (the last entry), `CommittedByteLength`, `Sequence`, `StorageDurability` and `IsMaterialized`. |
| `IsPoisoned` | True after a failed write. |
| `DefaultStorageFactory` | The plain file storage. |
| `DisposeAsync()` | Waits for the current append, then closes the file. |

An append is refused with `InvalidEntry` when it is empty or contains a header, a duplicate id, a parent that isn't in the file yet, or an assistant message whose `stopReason` is `pending`. A store doesn't choose parents or ids for you: set `parentId` to the entry you are continuing from. `SessionLogAppendResult` has `Entries` (as stored), `Snapshot`, `Sequence`, `ByteOffset`, `ByteLength`, `Accepted`, `Flushed`, `DurableCheckpointAcknowledged` and `CheckpointAcknowledged`.

If a write or flush fails, `SessionLogStoreException` says at which stage (`WriteFailed`, `FlushFailed`, `DurableFlushFailed`, `CheckpointFailed`), whether bytes may have reached the file (`MayHaveWritten`) and whether the disk flush completed (`DurableFlushCompleted`). The store is then poisoned and refuses further appends; dispose it and read the file again to see what was kept. Other failures are `InvalidEntry`, `InvalidLog`, `ResourceLimit`, `OpenFailed`, `CleanupFailed`, `Poisoned` and `Disposed`.

| `SessionLogStoreOptions` | Default | Meaning |
| --- | --- | --- |
| `ReaderOptions` | reader defaults | Limits for opening the file and for what it may grow to. |
| `MaximumBatchRecords` | 256 | Entries in one append. |
| `StorageFactory` | the file system | Where the bytes go: an `ISessionLogStorageFactory`. |
| `JavaScriptSerialization` | `false` | Write each record as Pi's `JSON.stringify` would: raw non-ASCII text and JavaScript number spelling. |

### Lazy and in-memory storage

`SessionStorageBackend` is an `ISessionLogStorageFactory` for one session folder, in one of two modes:

| `SessionStorageMode` | Behaviour |
| --- | --- |
| `LazyLocal` | Keeps a new session in memory until a user or assistant message is appended, then creates the file. The folder must exist. This is how Pi avoids empty session files. |
| `InMemory` | Never touches the disk for files in the folder, as with `--no-session`. |

Pass it as `StorageFactory`. The same object also serves as the file system for `SessionCatalog`, `SessionBranchPublisher` and `SessionCopyService`, so they see its unsaved sessions. `SessionStorageBackendOptions` bounds it to 2,048 files, 16,777,216 bytes per file and 67,108,864 bytes in memory. A snapshot's `StorageDurability` is `VolatileMemory` or `DeferredLocalFile` until the bytes are in a real file, and `LocalFileFlush` after.

To store sessions somewhere else, implement `ISessionLogStorageFactory.OpenAsync(path, createNew, cancellationToken)` and `ISessionLogStorage`: a seekable `ReadStream`, `Durability`, `Length`, `PositionForAppend`, `WriteAsync`, `FlushAsync`, `FlushToDisk` and `BeforeCheckpointAsync`.

## Fork a branch into a new file

`SessionBranchPlanner` builds a new session from a branch of an old one, and `SessionBranchPublisher` writes it without overwriting anything. The coding agent forks sessions with these two classes:

```csharp
using System.Collections.Immutable;
using System.Globalization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

var planner = new SessionBranchPlanner();
var labelCount = planner.GetRequiredLabelCount(entries, entryId, SessionForkPosition.At);
ImmutableArray<string> labelIds = [.. Enumerable.Range(0, labelCount).Select(_ => Guid.NewGuid().ToString("N")[..8])];
var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
var sessionId = Guid.CreateVersion7().ToString();

var plan = planner.Fork(new SessionForkPlanRequest(header, entries, entryId, SessionForkPosition.At,
    sessionId, timestamp, ParentSession: sourcePath, LabelEntryIds: labelIds));

var destination = Path.Combine(Path.GetDirectoryName(sourcePath)!,
    timestamp.Replace(':', '-').Replace('.', '-') + "_" + sessionId + ".jsonl");
await using var published = await new SessionBranchPublisher().PrepareAsync(plan, destination);
published.CommitAttachment();
```

`SessionForkPosition.At` keeps the branch up to and including the entry. `Before` keeps the branch up to its parent and must name a user message; `plan.SelectedText` then holds that message's text, which Pi puts back in the editor. The new file copies the branch's entries without its `label` entries, then adds a fresh `label` entry at the end for each label whose target it kept. That is why you get label ids ready first. The plan also has `Header`, `Entries`, `LeafId`, `SourceLeafId`, `HasConversation` and the encoded `JsonlBytes`.

`PrepareAsync` checks the plan, writes a temporary file in the destination folder, flushes it and moves it into place. The destination must be an absolute path that doesn't exist yet. The returned `PublishedSessionBranch` owns the new file: call `CommitAttachment` once you are using it, because disposing it uncommitted deletes the file. Failures throw `SessionBranchPlanException` or `SessionBranchPublishException`, which says whether the file was published and whether a temporary file may remain.

## Plan a compaction

The `Compaction` namespace decides what to summarize and builds the summary request. It doesn't call a model or write the `compaction` entry: [`PersistentAgentSession`](/docs/embed/coding-agent/) does both.

```csharp
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Context;

var context = new SessionContextProjector().ProjectLatest(entries);
var settings = new SessionCompactionSettings();
var estimate = SessionCompactionTokenEstimator.EstimateProjectedContextTokens(context);
if (SessionCompactionTokenEstimator.ShouldCompact(estimate.Tokens, contextWindow, settings) &&
    new SessionCompactionPlanner().Prepare(context, settings) is { } plan)
{
    var request = SessionSummaryRequestBuilder.History(plan, model, sessionId);
    Console.WriteLine($"summarize {plan.MessagesToSummarize.Length} messages, keep from {plan.FirstKeptEntryId}");
    Console.WriteLine($"{request.SystemPrompt.Length + request.Prompt.Length} characters, up to {request.MaximumOutputTokens} tokens");
}
```

| `SessionCompactionSettings` | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Whether `ShouldCompact` can say yes. |
| `ReserveTokens` | 16,384 | Compact when the context is within this many tokens of the window. |
| `KeepRecentTokens` | 20,000 | Roughly how many recent tokens to keep unsummarized. |

These match Pi's `compaction` settings.

| Member | Meaning |
| --- | --- |
| `SessionCompactionTokenEstimator.EstimateTokens(message)` | Pi's estimate: characters divided by 4, and 4,800 characters for an image. |
| `SessionCompactionTokenEstimator.EstimateContextTokens(messages)` | The last assistant message's reported usage plus an estimate for the messages after it. |
| `SessionCompactionTokenEstimator.EstimateProjectedContextTokens(context)` | The same for a projection, ignoring usage reported before the latest edit or compaction. |
| `SessionCompactionTokenEstimator.ShouldCompact(tokens, contextWindow, settings)` | `tokens > contextWindow - ReserveTokens`, when enabled. |
| `SessionCompactionPlanner.Prepare(context, settings)` | Picks the cut point. Returns `null` when there is nothing to compact. |
| `SessionCompactionPlanner.PrepareWithBoundary(context, firstKeptEntryId, settings)` | Uses your cut point; `null` keeps nothing. |
| `SessionBranchSummaryPlanner.Collect(entries, oldLeafId, targetId)` | The entries of the branch you are leaving, after the common ancestor. |
| `SessionBranchSummaryPlanner.Prepare(entries, tokenBudget, commonAncestorId)` | The messages to summarize for a branch summary. |
| `SessionSummaryRequestBuilder.History`, `.TurnPrefix`, `.Branch` | Build a `SessionSummaryRequest` with Pi's prompts. |
| `SessionSummaryRequestBuilder.SerializeConversation(messages)` | The conversation as the text Pi summarizes. |

`SessionCompactionPlan` has `FirstKeptEntryId`, `MessagesToSummarize`, `TurnPrefixMessages` and `IsSplitTurn` (a cut in the middle of a turn, which needs a second `TurnPrefix` summary), `TokensBefore`, `PreviousSummary`, `FileOps` and the `Settings` it used. `FileOps.ComputeFileLists()` and `SessionFileOperations.FormatFileOperations` produce the read and modified file lists Pi appends to a summary. `SessionSummaryRequest` has `Kind`, `Model`, `SystemPrompt`, `Prompt`, `MaximumOutputTokens`, `ThinkingLevel`, `SessionId` and `CacheRetention`; `SessionSummaryRequestOptions` adds custom instructions and the model's limits. Failures throw `SessionCompactionException`.

## List sessions

`SessionCatalog` lists the sessions in one or more folders. It reads only the header line of each `.jsonl` file directly in each folder, so listing stays fast:

```csharp
using PiSharp.Sessions.Lifecycle;

var catalog = new SessionCatalog([new SessionCatalogStore("project", sessionDirectory)]);
string? cursor = null;
do
{
    var page = await catalog.ListAsync(new SessionCatalogQuery(PageSize: 32, Cursor: cursor));
    foreach (var item in page.Items)
        Console.WriteLine($"{item.CreatedTimestamp}  {item.SessionId}  {item.WorkingDirectory}  {item.FileName}");
    cursor = page.NextCursor;
} while (cursor is not null);
```

Each `SessionCatalogStore` has an `Id` (letters, digits, `-` and `_`, up to 64) and an absolute `Directory`. Sessions come newest first by file modification time.

| Member | Meaning |
| --- | --- |
| `ListAsync(query)` | One page. `SessionCatalogQuery` has `PageSize` (default 32), `Cursor` from the previous page, and `WorkingDirectory` to keep only sessions for that folder. |
| `FindAsync(key)` | Finds an item again by its `Key`. |
| `ValidateOpenedHeader(item, header)` | Throws `SessionChanged` when the file you opened is no longer the session that was listed. |
| `CwdGroup(cwd)` | The comparison key for working folders: Windows paths compare without case and with either slash. |

A `SessionCatalogItem` has `Key`, `StoreId`, `FileName`, `Path`, `SessionId`, `CreatedTimestamp`, `WorkingDirectory`, `CwdGroup`, `ParentSessionPath`, `FileBytes`, `ModifiedUtcTicks` and `HeaderFingerprint`. A page also reports `SkippedFiles` (unreadable or invalid headers, links) and `UnavailableStores`. `SessionCatalogOptions` limits folder entries (2,048), stores (32), header bytes (65,536) and lines (32), path length (4,096) and page size (128).

## Copy, migrate and archive

`SessionCopyService` copies a session file to a new path, optionally migrating it from an older version. It never changes the source and never overwrites the destination:

```csharp
using PiSharp.Sessions.Import;

var service = new SessionCopyService();
var inspection = await service.InspectAsync(sourcePath);
Console.WriteLine($"version {inspection.SourceVersion}, can copy: {inspection.CanPublishCurrent}");
foreach (var diagnostic in inspection.Diagnostics) Console.WriteLine(diagnostic.Code);

var result = await service.CopyAsync(new SessionCopyRequest(sourcePath, destinationPath, SessionCopyFormat.CompatibleCurrentJsonl));
Console.WriteLine($"{result.Status}: {result.OutputBytes} bytes, sha256 {result.OutputSha256}");
```

| `SessionCopyFormat` | Result |
| --- | --- |
| `NativeExact` (default) | The exact bytes of a valid version 3 file. |
| `CompatibleCurrentJsonl` | The records migrated to version 3 and written again. Use it for version 1 and 2 files. |
| `NativeArchiveExact` | The exact bytes of any completely read file, even a damaged one. The copy is for keeping, not for resuming. |

Both paths must be absolute, the destination must not exist, and its folder must. The copy goes through a temporary file that is moved into place. `Status` is `Blocked` when the format can't be produced (the diagnostics say why), `Published`, or `PublishedWithCleanupFailure`. I/O failures throw `SessionCopyException`, which says how far publication got.

Version 1 files have no entry ids. Pi makes up random ones when it migrates; PiSharp doesn't generate ids, so pass one per entry in `SessionCopyRequest.V1EntryIds` (or `InspectAsync`'s `v1EntryIds`), or migration stops with `IdPlanRequired`. `SessionEntryMigration` does the conversion on records in memory, with `Migrate(records, v1EntryIds)` and `MigrateArray(array, v1EntryIds)`.

The older `pisharp session copy-inspect`, `session copy` and `session migrate` commands wrap this service.

## Limits

Every class takes an options record, and every default is a limit, not a target. Real sessions with screenshots or long tool output can exceed the defaults, which are tighter than Pi's. Raise them together: the reader's file and line limits, the codec's record limits, and the projector's character limits.

| Options | Used by |
| --- | --- |
| `SessionEntryCodecOptions` | `SessionEntryCodec`, and through `SessionLogReaderOptions.CodecOptions` everything that reads |
| `SessionLogReaderOptions` | `SessionLogReader`, `SessionLogStore`, `SessionCopyService`, `SessionBranchPublisher` |
| `SessionLogStoreOptions` | `SessionLogStore` |
| `SessionContextProjectionOptions` | `SessionContextProjector`, `SessionHistoryProjector`, `SessionSystemReplay`, `SessionTreeQueries` |
| `SessionTreeQueryOptions` | `SessionTreeQueries` |
| `SessionCompactionPlanningOptions` | `SessionCompactionPlanner`, `SessionBranchSummaryPlanner` (100,000 entries and messages, 8,388,608 characters) |
| `SessionBranchPlanOptions` | `SessionBranchPlanner` (100,000 entries, 16,777,216 output bytes) |
| `SessionBranchPublishOptions` | `SessionBranchPublisher` |
| `SessionCatalogOptions` | `SessionCatalog` |
| `SessionCopyOptions` | `SessionCopyService` (16,777,216 output bytes) |
| `SessionStorageBackendOptions` | `SessionStorageBackend` |

`SessionBranchPublisher` and `SessionCopyService` refuse reader limits above 536,870,888 bytes (`SessionBranchPublisher.MaximumInputBytes`), the largest file Pi can read.

## How it differs from Pi

- **Damaged lines stop the read.** Pi skips lines it can't parse. PiSharp reports the first one and keeps the valid records before it.
- **Stricter JSON.** Duplicate property names, comments, trailing commas, byte order marks and invalid UTF-8 are errors.
- **No generated ids.** Migrating a version 1 file needs ids from you, and forking needs label ids from you.
- **Bounded by default.** Every operation has limits you can raise, and the defaults are lower than Pi's.

Pi's own session code is [`session-manager.ts`](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/src/core/session-manager.ts).
