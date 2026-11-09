---
title: Anthropic message threads
description: Continuation state, request compatibility, cleanup and fallback behavior.
---

:::note[New in 1.5.0.1]
These changes ship in PiSharp 1.5.0.1. The Pi baseline remains 1.5.0; bounded continuation state is an optimization over the complete transcript.
:::

Anthropic request factories can retain bounded process-local message continuation state. The [thread implementation](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessageThreads.cs) stores transcript hashes and reply IDs, not prompts or credentials. Its default capacity is 10,000 sessions. It requires a session identity and compatible projected requests; support defaults to the direct Anthropic endpoint and can be controlled by `SupportsMessageThreads` / model `compat.supportsMessageThreads`.

On a matching transcript prefix, the next request sends only the suffix and the previous reply ID. Changes to the model, endpoint or relevant request settings create a new thread. Prefix hashing normalizes cache controls and equivalent text forms. A reply ID is reserved before network execution so concurrent calls cannot consume it twice. Failed, aborted or unreplayable replies do not become continuation state.

## Own the cache lifetime

`AnthropicMessageThreadStore` is public in `PiSharp.AI.Protocols.AnthropicMessages`. Request-factory options accept `ThreadStore`; otherwise they use `AnthropicMessageThreadStore.Shared`.

```csharp
using PiSharp.AI.Protocols.AnthropicMessages;

var threads = new AnthropicMessageThreadStore(maximumSessions: 1000);
// Pass threads as ThreadStore when constructing the Anthropic request factory.
threads.Cleanup(sessionId); // invalidates pending replies for this session too
// threads.Cleanup();      // clears all sessions in this store
```

Cleanup prevents a late reply from resurrecting a removed session. This is a request optimization, not durable conversation storage. Reopening a process starts without its continuation cache and retains the complete transcript path.

## Compatibility, retries and capture

Known thread rejections trigger bounded full-request fallback. Unsupported requests disable continuation for the session; other recognized thread conflicts rebuild safely. The original full request remains available for replay. OAuth projection and cache-warming use the same compatibility policy. See [authenticated factory](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesAuthenticatedRequestFactory.cs) and [key factory](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesKeyAuthRequestFactory.cs).

Prepared payloads, metadata names, hooks, optional captures and public wire round trips preserve UTF-16 JSON values, including lone surrogates. Malformed payload and resource-limit checks remain active. Offline qualification is described in the [acceptance report](https://github.com/jkelly/PiSharp/blob/main/AI_DOCS/gap-implementation-results.md); it is not a live-provider validation claim.
