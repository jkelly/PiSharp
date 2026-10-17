// Prints Fixtures/piai-utils.upstream.json: what the pinned pi-ai (the Pi commit given with --commit, one of tools/DurableFixtures/pin.mjs; packages/ai/src/utils)
// returns for the inputs PiAiUtilsCases.cs replays: isContextOverflow, isRetryableAssistantError, retryDelayMs, getCurrentTools,
// declarationsEqual/toToolDeclaration, estimateMessageTokens and calculateContextTokens.
// Usage, with a Pi checkout at a pinned commit and its node_modules (pi-ai is that release's published build; see
// tools/DurableFixtures/capture-all.mjs, which runs every capture into the commit's fixture set):
//   node --no-warnings --experimental-strip-types capture-piai-utils.mjs <pi checkout> --commit <sha> > piai-utils.upstream.json
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

import { pinnedArguments } from '../../../tools/DurableFixtures/pin.mjs';

const pin = pinnedArguments(process.argv.slice(2), 'capture-piai-utils.mjs <pi checkout> --commit <sha>');
const { pi } = pin;
const at = (path) => pathToFileURL(join(pi, 'packages/ai/src/utils', path)).href;
const { isContextOverflow } = await import(at('overflow.ts'));
const { isRetryableAssistantError, retryDelayMs } = await import(at('retry.ts'));
const { getCurrentTools, declarationsEqual, toToolDeclaration } = await import(at('transcript.ts'));
const { estimateMessageTokens, calculateContextTokens } = await import(at('estimate.ts'));

const usage = (input, output, cacheRead = 0) => ({ input, output, cacheRead, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } });
const msg = (stopReason, errorMessage, provider = "openai", u = usage(0, 0)) => ({ role: "assistant", content: [], api: "x", provider, model: "m", usage: u, stopReason, ...(errorMessage === undefined ? {} : { errorMessage }), timestamp: 1 });
const errors = [
  "prompt is too long: 213462 tokens > 200000 maximum", "413 {\"error\":{\"type\":\"request_too_large\",\"message\":\"Request exceeds the maximum size\"}}",
  "Your input exceeds the context window of this model", "Requested token count exceeds the model's maximum context length of 131072 tokens",
  "Input length (265330) exceeds model's maximum context length (262144).", "The input token count (1196265) exceeds the maximum number of tokens allowed (1048575)",
  "This model's maximum prompt length is 131072 but the request contains 537812 tokens", "Please reduce the length of the messages or completion",
  "This endpoint's maximum context length is 4096 tokens. However, you requested about 5000 tokens", "Input length 10 exceeds the maximum allowed input length of 8 tokens.",
  "The input (10 tokens) is longer than the model's context length (8 tokens).", "the request exceeds the available context size, try increasing it",
  "tokens to keep from the initial prompt is greater than the context length", "prompt token count of 10 exceeds the limit of 8",
  "invalid params, context window exceeds limit", "Your request exceeded model token limit: 10 (requested: 12)",
  "Prompt has 1,000 tokens, but the configured context size is 800 tokens", "400 status code (no body)", "413 (no body)",
  "Prompt contains 100 tokens ... too large for model with 50 maximum context length", "{\"code\":\"1261\",\"message\":\"Prompt too long\"}",
  "Prompt exceeds max length", "Range of input length should be [1, 30720]", "prompt too long; exceeded max context length by 10 tokens",
  "ThrottlingException: Too many tokens, please wait before trying again.", "Throttling error: Too many tokens", "Service unavailable: try later",
  "rate limit: too many tokens", "Too many requests", "context_length_exceeded", "token limit exceeded", "too many tokens",
  "PROMPT IS TOO LONG", "prompt ıs too long", "input token count\nexceeds the maximum", "input token count  exceeds the maximum",
  "exceeds the limit of ١٢", "maximum context length is 12 tokens", "Input length (1) exceeds model's maximum context length (2)",
  "503 Service Unavailable", "Invalid request", "billing: 429", "insufficient_quota", "Connection\nrefused", "connection refused",
  "connection refused", "ResourceExhausted", "resourceexhausted", "timed out", "Timeout", "stream ended before message_stop",
  "Provider returned error", "ſervice unavailable", "serviceKunavailable", "RATE_LIMIT", "rate-limit", "GoUsageLimitError 503",
  "upstream connect error", "fetch failed", "socket hang up", "terminated", "you can retry your request", "", "overloaded_error",
];
const overflow = [];
for (const error of errors) {
  overflow.push({ message: msg("error", error), contextWindow: null, result: isContextOverflow(msg("error", error)) });
  overflow.push({ message: msg("error", error, "cerebras"), contextWindow: null, result: isContextOverflow(msg("error", error, "cerebras")) });
}
const silent = [["stop", usage(100, 5), 99], ["stop", usage(50, 5, 50), 100], ["stop", usage(50, 5, 51), 100],
  ["length", usage(99, 0), 100], ["length", usage(98, 0), 100], ["length", usage(99, 1), 100], ["stop", usage(1000, 0), 0], ["error", usage(1000, 0), 10],
  ["length", usage(99, 0, 0), Number.NaN]];
for (const [stop, u, window] of silent) {
  const m = msg(stop, undefined, "x", u);
  overflow.push({ message: m, contextWindow: Number.isNaN(window) ? "NaN" : window, result: isContextOverflow(m, window) });
}
const retry = [];
for (const error of errors) {
  for (const stop of ["error", "stop", "aborted"]) retry.push({ message: msg(stop, error), result: isRetryableAssistantError(msg(stop, error)) });
}
retry.push({ message: msg("error"), result: isRetryableAssistantError(msg("error")) });
const delays = [];
const policies = [[2000, undefined], [1, undefined], [1.5, undefined], [0, undefined], [60000, undefined], [1000, 5000], [2000, 0], [1e300, undefined]];
for (const [base, max] of policies)
  for (const attempt of [0, 1, 2, 3, 5, 10, 30, 60, 1100])
    delays.push({ baseDelayMs: base, maxAgentDelayMs: max ?? null, attempt, result: retryDelayMs({ baseDelayMs: base, maxAgentDelayMs: max }, attempt) });
const tool = (name, description = name, extra = {}) => ({ name, description, parameters: { type: "object", properties: {} }, ...extra });
const transcripts = [
  [],
  [{ role: "system", content: "", toolsAdded: [tool("a"), tool("b")], timestamp: 1 }],
  [{ role: "system", content: "", toolsAdded: [tool("a"), tool("b")], timestamp: 1 }, { role: "user", content: "x", timestamp: 2 },
    { role: "system", content: "", toolsAdded: [tool("a", "changed")], timestamp: 3 }],
  [{ role: "system", content: "", toolsAdded: [tool("a"), tool("b"), tool("c")], timestamp: 1 },
    { role: "system", content: "", toolsRemoved: [{ name: "a" }], toolsAdded: [tool("a", "again")], timestamp: 2 }],
  [{ role: "system", content: "", toolsAdded: [tool("a"), tool("b")], timestamp: 1 }, { role: "system", content: "", toolsRemoved: [{ name: "b" }, { name: "z" }], timestamp: 2 },
    { role: "assistant", content: [], toolsAdded: [tool("q")], timestamp: 3 }, { role: "system", content: "", toolsAdded: [tool("b", "b", { constrainedSampling: true })], timestamp: 4 }],
].map((messages) => ({ messages, result: getCurrentTools(messages) }));
const pairs = [[tool("a"), tool("a")], [tool("a"), tool("a", "x")], [tool("a", "a", { extra: 1 }), tool("a")], [tool("a", "a", { constrainedSampling: true }), tool("a")],
  [{ name: "a", parameters: { properties: {}, type: "object" }, description: "a" }, tool("a")]]
  .map(([left, right]) => ({ left, right, result: declarationsEqual(left, right), declaration: toToolDeclaration(left) }));
const estimates = [
  { role: "user", content: "hello world", timestamp: 1 },
  { role: "user", content: [{ type: "text", text: "abc" }, { type: "image", data: "xx", mimeType: "image/png" }], timestamp: 1 },
  { role: "assistant", content: [{ type: "text", text: "hi there" }, { type: "thinking", thinking: "hmm" }, { type: "toolCall", id: "1", name: "read", arguments: { path: "a.txt" } }],
    api: "x", provider: "x", model: "m", usage: usage(0, 0), stopReason: "toolUse", timestamp: 1 },
  { role: "toolResult", toolCallId: "1", toolName: "read", content: [{ type: "text", text: "contents here" }], isError: false, timestamp: 1 },
  { role: "system", content: "", sections: { a: "<a>\nx\n</a>", b: null, c: "yy" }, toolsAdded: [tool("a")], toolsRemoved: [{ name: "b" }], timestamp: 1 },
  { role: "system", content: "base", timestamp: 1 },
].map((message) => ({ message, result: estimateMessageTokens(message) }));
const contextTokens = [usage(1, 2, 3), { ...usage(1, 2, 3), totalTokens: 50 }, usage(0, 0)].map((u) => ({ usage: u, result: calculateContextTokens(u) }));
// uuidv7() is time-dependent; the fixture pins only its shape (null here).
console.log(JSON.stringify({ overflow, retry, delays, transcripts, pairs, estimates, contextTokens, uuid: null }));
