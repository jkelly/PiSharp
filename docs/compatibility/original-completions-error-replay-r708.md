# Original Completions error replay metadata (R708)

Source-only authored leaf; synthetic controls are UNEXECUTED. No compiler, tests,
Node, SDK invocation, network, credentials, package acquisition or live API was used.

The original provider collects valid `reasoning_details`, merges consecutive
summary/text entries, and writes the collected array into `thinkingSignature` in
its catch before publishing error. The native mapper already collected and merged
those details, but wrote their final signature only while finalizing successful
blocks. A late source fault or cancellation therefore retained the initial field
name (or empty signature) rather than the collected replay metadata.

`OpenAICompletionsWireSource` now adds that signature to its immutable error/abort
terminal message. This also reaches owned terminal emission/drain snapshots through
the existing terminal capture route. It does not publish a successful
`ThinkingEnded` on failure. Signature projection uses existing depth/content limits;
the escaped retained value is charged once and cached across repeated cancellation
finalization. If the overlay exhausts the configured budget, the original sanitized
error/abort terminal settles with its original diagnostic and existing signature.
Success finalization, source exception normalization and cleanup ownership are unchanged.

Original reference: Pi 0.99.1 commit
`d86654abb8862e201933517d6f1fce9f88dd117f`,
[`packages/ai/src/api/openai-completions.ts`, catch lines 702–705](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/api/openai-completions.ts#L702).
The actual local file inspected has SHA256
`7c1bc51ddf740ce69b371e1ae391fcdcecde26852428451e7b17eeabdcfb4eb4`.
OpenAI SDK is pinned to `7.19.0` by the original package manifest and lockfile entry
`packages/ai/node_modules/openai` (the root also has OpenAI 6.40.0);
lockfile SHA256 `e245cabcefdd23d1ab3cfd21db492e21ca11b7ef4a20f6304aead4dc2b5b569d`.
This patch derives from original provider source, not an SDK wire comparison.

Standalone controls are in
`tests/PiSharp.CompletionsAnchored.Tests/Controls/ErrorThinkingReplayControls.cs`.
The existing anchored project excludes this directory; no shared runner or project
was changed. Root qualification can invoke `ErrorThinkingReplayControls.RunAsync`
from an isolated host. The controls cover an in-flight late source fault with raw
exception identity and faulted task evidence, actual CTS cancellation and a cancelled
pending source task, exact retained merged summary/encrypted replay signature,
error emission snapshot, one owned enumerator close, absence of successful thinking
end on failure, bounded overlay rejection, and unchanged success finalization.

Base source composition is `072ee741a2d197860a49da9fcb981a3a65ff9f96` (R676).
Selected accepted release remains `a9df14f95d530157de021da81bc9fd17f69e2fd0`.
Root owns compilation, execution, independent review and acceptance. Until those
receipts exist this leaf establishes authored implementation only; original SDK
wire/error capture comparison remains open per R648.
