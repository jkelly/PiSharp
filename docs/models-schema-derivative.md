# Models schema derivative

`src/PiSharp.CodingAgent/Schemas/models.schema.json` is a reviewed derivative of Pi v1.5.0's models schema. It retains the schema identifier for configuration compatibility; the current bytes are not claimed to be the original published file.

The original is preserved byte-for-byte in `tests/PiSharp.Models.Tests/Fixtures/upstream-v1.5.0-models.schema.json`, obtained from the implementation branch's unchanged source baseline. Its Git blob is `c51a82c358ef1d92d4b2a2481be1e9d37258057e` and SHA-256 is `c9ed21414935bab8de93347970d035f997b5f8f28a2a4e5e0b501820fbb35f75`.

The current LF-normalized derivative has Git blob `99e41024642d346249690423b78f68dd3688bcd0` and SHA-256 `d523cdf5b7535b7f4601f19812635df9312376cafeda4e93f1a75c6b82df8c1c`. LF matches the repository's existing JSON attributes. The only semantic additions are three properties of `$defs.ProviderCompat`: `requiresNonNullAssistantContent`, `requiresToolsWithToolHistory`, and `promptCacheKeyMode` (`auto`, `enabled`, `disabled`). They expose the reviewed I04 runtime compatibility behavior. No schema was published to the retained URL.

Models tests bind the original fixture to the original blob, independently bind the current embedded derivative to its SHA-256, compare the full schemas against exactly those additions, and validate accepted/rejected extension values. The other three embedded configuration schemas retain their original byte checks.
