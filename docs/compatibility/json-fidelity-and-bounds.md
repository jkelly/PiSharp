# JSON fidelity and memory bounds

Pi 1.1.0 holds every JSON value as a JavaScript value: `JSON.parse` has no depth limit, keeps an escaped lone surrogate as that UTF-16
code unit, reads every number as a binary64, and bounds a record only by V8's longest string. This page records how the Pi entry
(plain `pisharp`, `-p`, `--mode json|rpc`) follows that, and the bounds that remain.

## Lone surrogates

A `JsonData` carries a lone surrogate of a string value or an object name as its `JSON.stringify` escape (`\ud800`); `JsonUtf16` reads,
writes, compares and serializes such values (System.Text.Json's own `GetString`, `Name`, `WriteTo` and writers refuse or replace them;
`JsonUtf16.MutableNode` builds a node tree that holds them). End to end:

| Where | Behaviour (as Pi) |
|---|---|
| RPC input (`--mode rpc`) | kept in every string value and object name; echoed and written back as lowercase `\uXXXX` |
| Prompt text, steering and follow-up | kept in the user message |
| Streamed text and tool-call arguments | kept, names included (the SDK's `JSON.parse`, then `parseStreamingJson`) |
| Session read and write | kept; a session line holding one opens (formerly refused) |
| Provider requests | message text through `sanitizeSurrogates` (dropped); tool-call arguments (names and strings) kept, escaped, for every built-in converter: anthropic-messages, openai-completions, openai-responses (Azure, Codex), mistral-conversations, google-generative-ai, google-vertex and bedrock-converse-stream (`sanitizeBedrockDocument` drops only empty keys; the AWS SDK's `JSON.stringify` escapes the rest) |
| pi-messages and extension APIs (`streamSimple`) | the context unchanged, every lone surrogate kept (escaped on the bridge) |
| Tool results (built-in, extension, MCP) | kept in text, details and their names (`agent-loop.ts` keeps whatever a tool returns); the session line and events carry the escape |
| Extension events, handler contexts, registry values | kept (`runner.ts` emits whatever the session holds) |
| The `write` tool | written as U+FFFD, as Node's UTF-8 encoder writes it |

Remaining differences: the explicit `session …` and `rpc` verbs keep refusing them; a lone surrogate in a received openai-completions,
openai-responses or Codex stream event is refused (upstream keeps it).

## Nesting depth

`JsonData.MaximumDepth` is 1,000 levels (formerly 64, and 32 in several provider paths). JSON.parse has no limit, but Pi's own writes
recurse: V8's `JSON.stringify` gives up at about 1,700 nested objects (2,200 arrays) on Node 22's default stack, so Pi cannot store or
send a deeper value. PiSharp's readers, validators and writers recurse too; 1,000 levels (System.Text.Json's own writer default) keeps
every such walk inside a thread's stack. Session records, RPC input, streamed tool-call arguments and every provider's replay hold that
depth; an RPC line nested deeper is answered as a parse failure (`JSON nests deeper than 1000 levels`) and the stream goes on (formerly
65 levels ended it). Codex events and request bodies, Bedrock request bodies, the extension registry (registrations, session entries
handed to handlers, observations), the Node extension bridge (formerly 256), extension event dispatch, provider error bodies, Responses
reasoning signatures, the HTML export and the user's `models.json`, `settings.json`, `keybindings.json` and `auth.json` hold it too
(formerly 64), and so do the tool declarations a session records and sends (a registered tool's parameter schema, formerly
refused past 64 levels).

## Token counts

`TokenUsage` counts are binary64 numbers, read and written as JavaScript numbers (a whole count as an integer, a fraction as
`Number::toString` writes it). Every provider's usage parsing accepts a fraction and prices it with `calculateCost` in binary64, Bedrock
included (`(3 / 1000000) * 100` is `0.00030000000000000003`). The Anthropic simple path's context estimate, output cap and thinking
budget are JavaScript numbers as `estimateContextTokens`, `clampMaxTokensToContext` and `adjustMaxTokensForThinking` compute them (a
fractional usage leaves a fractional `max_tokens` and `budget_tokens`; formerly rounded up to whole tokens).

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
captured by running the installed Pi 1.1.0 CLI against a local fake provider), `tests/PiSharp.ProviderApis.Tests/JsonLeftoverCases.cs`
and `BedrockCases.cs` (Bedrock bodies and binary64 costs, Codex depth), `tests/PiSharp.ExtensionParity.Tests/JsonLeftoverCases.cs`
(extension tool results and deep values), `tests/PiSharp.Agent.Tests/ToolLoneSurrogateTests.cs` and
`tests/PiSharp.AnthropicSimple.Tests` (fractional estimate, cap and budget).
