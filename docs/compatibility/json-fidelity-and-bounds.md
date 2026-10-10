# JSON fidelity and memory bounds

Pi 1.1.0 holds every JSON value as a JavaScript value: `JSON.parse` has no depth limit, keeps an escaped lone surrogate as that UTF-16
code unit, reads every number as a binary64, and bounds a record only by V8's longest string. This page records how the Pi entry
(plain `pisharp`, `-p`, `--mode json|rpc`) follows that, and the bounds that remain.

## Lone surrogates

A `JsonData` carries a lone surrogate of a string value as its `JSON.stringify` escape (`\ud800`); `JsonUtf16` reads, writes, compares
and serializes such values (System.Text.Json's own `GetString`, `WriteTo` and writers refuse or replace them). End to end:

| Where | Behaviour (as Pi) |
|---|---|
| RPC input (`--mode rpc`) | kept in every string value; echoed and written back as lowercase `\uXXXX` |
| Prompt text, steering and follow-up | kept in the user message |
| Streamed text and tool-call arguments | kept (the SDK's `JSON.parse`, then `parseStreamingJson`) |
| Session read and write | kept; a session line holding one opens (formerly refused) |
| Provider requests | message text through `sanitizeSurrogates` (dropped); tool-call arguments kept, escaped, for anthropic-messages, openai-completions, openai-responses (Azure, Codex), mistral-conversations, google-generative-ai and google-vertex |
| The `write` tool | written as U+FFFD, as Node's UTF-8 encoder writes it |

Remaining differences: a lone surrogate in an object *name* becomes U+FFFD; bedrock-converse-stream and the other APIs drop a lone
surrogate from tool-call arguments rather than sending it escaped; a tool's own result text holding one is refused by the tool invoker;
the explicit `session …` and `rpc` verbs keep refusing them.

## Nesting depth

`JsonData.MaximumDepth` is 1,000 levels (formerly 64, and 32 in several provider paths). JSON.parse has no limit, but Pi's own writes
recurse: V8's `JSON.stringify` gives up at about 1,700 nested objects (2,200 arrays) on Node 22's default stack, so Pi cannot store or
send a deeper value. PiSharp's readers, validators and writers recurse too; 1,000 levels (System.Text.Json's own writer default) keeps
every such walk inside a thread's stack. Session records, RPC input, streamed tool-call arguments and every provider's replay hold that
depth; an RPC line nested deeper is answered as a parse failure (`JSON nests deeper than 1000 levels`) and the stream goes on (formerly
65 levels ended it).

## Token counts

`TokenUsage` counts are binary64 numbers, read and written as JavaScript numbers (a whole count as an integer, a fraction as
`Number::toString` writes it). Every provider's usage parsing accepts a fraction and prices it with `calculateCost` in binary64 (Bedrock
keeps its decimal cost arithmetic). The Anthropic simple path's integer context estimate rounds a fractional latest usage up.

## Memory bounds (Pi entry)

Pi bounds none of these records itself. Each is one JavaScript string, so V8's longest string, `buffer.constants.MAX_STRING_LENGTH` =
536,870,888 UTF-16 code units on 64-bit Node 22, is what Pi can actually process. PiSharp admits that many UTF-8 bytes where it counts
bytes (every ASCII record Pi can process fits; a non-ASCII record of more bytes is refused) and that many UTF-16 code units where it
counts characters.

| Bound | Before | Now | Why this value |
|---|---|---|---|
| Session line | 64 MiB | 536,870,888 bytes | one string (`session-manager.ts` splits the file string) |
| Session file | 64 MiB | 536,870,888 bytes | read whole into one string, as Pi reads it; also the reader's out-of-memory ceiling |
| Streamed event, content block | 16 MiB | 536,870,888 characters | one string in the SDK |
| One response, all streamed data | 64 MiB | 1,073,741,776 characters | out-of-memory ceiling: two of the longest strings (2 GiB of UTF-16) |
| Tool result text | 8 MiB | 536,870,888 characters | one string; its raw JSON stays within twice that and an `int` of UTF-8 bytes |
| RPC input line | 32 MiB | 536,870,888 bytes | one string per line (`rpc-mode.ts`) |
| RPC and JSON-mode output line | 32 MiB | 536,870,888 bytes | one `JSON.stringify` string per line |

The explicit `session …` and `rpc` verbs keep their bounds (64 MiB session line and file, 32 MiB RPC records, 8 MiB tool results).
Request bodies (64 MiB) and request entries (16 MiB) are unchanged.

Tests: `tests/PiSharp.CliParity.Tests/JsonFidelityCases.cs` (expected request messages, session lines, usage objects and file bytes
captured by running the installed Pi 1.1.0 CLI against a local fake provider).
