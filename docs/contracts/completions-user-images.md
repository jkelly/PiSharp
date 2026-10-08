# Completions inline user images

This completes the user-image request-projection criterion within original P2-08, with P2-02 transcript and P2-10 differential evidence. Shared contracts remain unchanged. The original 79 package scopes and eight phase gates remain open.

Pinned [Pi v0.99.1 `transformMessages`](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/api/transform-messages.ts#L12) first replaces user images for a model without image input with `(image omitted: model does not support images)`. Consecutive image blocks collapse to one placeholder. Literal placeholder text suppresses a following image; other text, including empty text, breaks the run before empty-text filtering. The provider-local nonpositional `CompletionsTranscriptProjectionOptions.ModelSupportsImages` supplies this capability; it defaults to false. The existing positional constructor and deconstruction remain intact. Configure it to true from the selected model's image-input capability. Automatic catalog-to-adapter capability wiring remains outside this criterion.

For an image-capable model, [`convertMessages`](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/api/openai-completions.ts#L1250) forwards each typed user image as `{type:"image_url",image_url:{url:"data:" + mimeType + ";base64," + data}}`. Mixed text/images, image-only messages and multiple images retain order; empty text blocks disappear. An empty content array disappears, while a user string, including an empty string, remains. The port forwards the two strings without base64 decoding, MIME inference, URL fetching, or additional image metadata. Unknown canonical fields remain retained in history and are omitted from this provider projection.

Native strict admission still requires typed string fields, unambiguous JSON and valid Unicode. Existing entry/input/block/depth/output-character/output-byte/payload limits cover images and the derived data URI. Cancellation precedes request acquisition. Source malformed JavaScript object coercions are outside this bounded typed-image criterion.

`capture-completions-user-images.mjs` runs the actual unchanged pinned provider and OpenAI SDK against 25 authored in-memory responses, verifies the existing lifecycle lock's 230 source files and every loaded module, and denies external fetch. Its additive capture is `fixtures/native/completions-user-images-source.json`; the original strict fixture, comparator and lock are unchanged. Reproduce into a fresh output file with the locked Node executable:

```powershell
node tools/ReferenceOracle/capture-completions-user-images.mjs <pinned-oracle-root> <fresh-output.json>
```

Native tests compare complete parsed request bodies against those actual source captures, including paired text-only/image-capable model controls, a post-tool bridge, consecutive-image runs and agent history bodies. Repeated construction retains canonical input. Two actual two-turn HTTP/agent tests retain canonical images through tool-result continuation with either forwarding or placeholder projection, join owned response cleanup before assistant commit, and await assistant/result sink barriers before tool effects and the next provider request.

The previous native test rejecting a user image was an observation of missing behavior. Its original red receipt is retained; the unsupported-content control now covers tool-result images. This is a deliberate implementation change, not an original source expectation change.

The successor [tool-image transcript contract](completions-tool-images.md) implements tool-result image placeholders, grouping and configured forwarding, and the literal-placeholder edge. Automatic capability wiring, native image-producing Agent/tool contracts, persistence/consumer qualification, other Completions/provider/model quirks, complete reader/queue/lifecycle qualification, truthful native SDK identity policy and integrated full native/physical-terminal gates remain mandatory and open. These units do not close P2-08, P2-10, P3 or an entire phase.
