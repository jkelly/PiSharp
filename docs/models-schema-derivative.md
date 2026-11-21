# Models schema derivative

Retired with the Pi 1.6.0 sync. `src/PiSharp.CodingAgent/Schemas/models.schema.json` is again a byte-for-byte copy of Pi's published file: `packages/coding-agent/schemas/models.schema.json` at v1.6.0 (commit `faac585884ae2c013a6b1400f3e9e77f10b34bac`), Git blob `832194451e28b45820b27d765c36c1288ad04a6b`, SHA-256 `b5679d7ad00fa34aa4f0b42aa7578ed85ae1fbf4b254ddeba5a4288e1c3c4f27`.

## History

From the Pi 1.5.0 sync until the Pi 1.6.0 sync the embedded file was a reviewed derivative of Pi v1.5.0's schema (LF-normalized Git blob `99e41024642d346249690423b78f68dd3688bcd0`, SHA-256 `d523cdf5b7535b7f4601f19812635df9312376cafeda4e93f1a75c6b82df8c1c`). It added three properties to `$defs.ProviderCompat` ahead of upstream: `requiresNonNullAssistantContent`, `requiresToolsWithToolHistory`, and `promptCacheKeyMode` (`auto`, `enabled`, `disabled`).

Pi 1.5.2 published the same three properties itself, with its own descriptions and `promptCacheKeyMode` as an `anyOf` of constants, and added `supportsMessageThreads`. The derivative was not rebased then, so it lacked `supportsMessageThreads`. Pi 1.6.0 reworded the `supportsMidConvoEffort` and `supportsMessageThreads` descriptions. Upstream's file now accepts and rejects the same values for the three former extensions, so the derivative has no remaining purpose.

## Tests

The original v1.5.0 file is preserved byte-for-byte in `tests/PiSharp.Models.Tests/Fixtures/upstream-v1.5.0-models.schema.json` (Git blob `c51a82c358ef1d92d4b2a2481be1e9d37258057e`, SHA-256 `c9ed21414935bab8de93347970d035f997b5f8f28a2a4e5e0b501820fbb35f75`).

`tests/PiSharp.Models.Tests/ConfigSchemaCases.cs` binds the embedded file to the v1.6.0 blob, binds the fixture to the v1.5.0 blob, and checks that the two differ by exactly the four added properties and the one changed description. `Sync160CatalogCases.cs` (`sync160.models-schema-v1.6.0`) checks the accepted and rejected values.
