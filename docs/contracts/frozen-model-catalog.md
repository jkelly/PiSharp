# Frozen provider JSON catalog reader

`PiSharp.AI.Catalogs.FrozenModelCatalog.ReadProviderJson(provider, utf8Json, options)` reads one caller-supplied provider shard in the released Pi catalog shape. It returns immutable model identities, ordinal lookup, API declarations, and owned raw provider/model/cost JSON. The reader performs no file, network, authentication, provider registration, transport dispatch or global-state operations. It imports no catalog automatically and leaves `ModelDescriptor` unchanged.

This is a bounded P2-05 catalog admission slice. Metadata declarations do not prove that a transport, model, image operation, classifier operation, reasoning feature or credential flow works. `DeclaredApi` and `DeclaredApis` deliberately describe the supplied data; the catalog contains no runtime-support flag.

`FrozenCatalogModel.DeclaresImageInput` is true exactly when the admitted model's `input` array contains the decoded, ordinal string `image`. The declaration is available for all three operation types. An image-generation model's `output` field, another model sharing its id, API spelling, compatibility fields and input limits do not supply it. This read-only property derives from already validated caller-owned metadata, without changing `ModelDescriptor`, model identity, raw JSON, admission rules or registration behavior. See [catalog image-input caller ownership](catalog-image-input.md) for the separate selected-model/transport binding work.

## Released shape and ownership

The pinned [generated catalog helpers](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/model-catalog.ts) read objects shaped as:

```json
{
  "declared-api": {
    "chat:model-id": {
      "type": "chat",
      "id": "model-id",
      "provider": "provider-id",
      "api": "declared-api",
      "name": "Model name",
      "baseUrl": "",
      "input": ["text"],
      "reasoning": false,
      "contextWindow": 64,
      "maxTokens": 32,
      "cost": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 }
    }
  }
}
```

The example is authored to illustrate the released shape, with synthetic limits and rates. The reader does not inject these values.

The [upstream model contracts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/types.ts) share id/name/provider/API/base URL/input/cost fields through `BaseModel`. Chat adds reasoning/context/max-token metadata, image adds output modalities including `image`, and classifier adds a context window. This type inheritance supplies no runtime defaults. Generated flattening helpers filter explicit `type` and retain model objects; their `_provider` argument does not fill missing fields. General [runtime model helpers](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/model-operations.ts) separately support legacy missing-type chat models; that fallback is outside this released-shard reader.

Admission requires explicit `chat`, `image`, or `classifier` type. Provider and API must equal the caller/group identities, and each key must equal `type:id`. Identity is provider/type/id: the same id may exist for different operation types. Duplicate identities across API groups are rejected. All string comparisons and lookups use ordinal identity; no case, URL, provider or ID normalization occurs. A nonempty provider must contain at least one model; empty API groups may coexist with populated groups.

Every object is checked recursively for duplicate decoded property names, including escaped aliases. Unknown API names and unknown model fields remain raw metadata. Unknown model types are rejected with `UnsupportedModelType`. Required released fields follow the structural checks in the pinned [model-data validator](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/scripts/model-data.ts); identity strings are additionally required to be nonempty. Image output must include `image`; chat/classifier cannot declare an output field. Optional tiers, headers, thinking maps, compatibility flags, sampling defaults, input limits and future fields remain unmodified JSON. Their operational meaning is not validated here.

The reader copies the supplied UTF-8 buffer before parsing. `JsonData` clones sever document lifetimes and mutable input ownership. Immutable arrays and the private immutable lookup retain encounter order without exposing mutable collections. `Raw` preserves the provider object and `FrozenCatalogModel.Raw`/`Cost` preserve owned field values, array order, explicit null, absent fields, opaque strings and exact numeric lexemes. Outer whitespace is outside a JSON element's raw text; `ProviderJsonSha256` hashes the complete supplied UTF-8 bytes, including that whitespace. This hash records caller input identity and makes no claim of official provenance by itself.

## Numeric admission and limits

The four required cost rates must be JSON numbers that pass a finite binary64 admission check, matching the released validator's finite-number profile. Required context windows and chat max-token values must additionally be positive. The temporary numeric check is never returned or substituted into retained JSON. High-precision decimals and integers above 2^53 retain their original lexemes; this is raw-data preservation, not a claim that their mathematical value equals JavaScript's rounded `Number`. Finite negative rates are retained because the released validator does not impose nonnegative rates. This class performs no monetary arithmetic or pricing-tier selection.

Required numeric overflow, such as `cost.input: 1e400`, is rejected as `UnsupportedNumber`. Uninterpreted unknown numeric fields remain raw JSON within the lexical budget, even when outside binary64 range. JSON syntax, bounded lexeme preservation and operational numeric interpretation are separate constraints.

Defaults are 2,097,152 input UTF-8 bytes, 32 container levels, 4,096 models, 65,536 total object properties, 65,536 decoded UTF-16 code units per string/property name/provider argument, and 256 characters per numeric token. Limits must be positive; depth must be at most 64. The token pass enforces budgets and recursive property uniqueness before owned model cloning. Exact boundary values are allowed; overflow raises `ResourceLimit`. A UTF-8 byte budget is distinct from a string-character budget.

JSON comments, trailing commas, malformed/incomplete JSON and multiple top-level values are rejected. Unpaired escaped UTF-16 strings are rejected as `UnsupportedUnicode`, matching the native string-extraction limitation rather than silently replacing them. Paired Unicode and ordinary opaque strings are retained. Errors use typed `CatalogReadFailure` values and fixed messages, without rejected field names, model IDs, payloads or inner parser exceptions. Invalid option configurations use a fixed `ArgumentOutOfRangeException` diagnostic.

## Evidence and tests

`FrozenModelCatalogTests.Register(List<(string Name, Func<Task> Run)> tests, string repo)` adds nine groups for typed lookup/declarations, image-input declarations and operation identities, exact number and unknown-field preservation, identities/recursive duplicates, released shape/numeric admission, every configured boundary, ownership, sanitized errors/Unicode, and the real released catalog. The image-input contribution's runtime qualification is pending the lead's execution window. The lead owns registration in the aggregate helper and the shared build/run window.

The real artifact test uses the committed [npm package evidence](../compatibility/released-ai-package.md), acquisition commit `a467501263a3a1622f79fe3540b36471a110f286`. It hashes `pi-ai-0.99.1.tgz` before in-memory `GZipStream`/`TarReader` inspection, checks packaged manifest SHA-256 `9fbcd337d4bd414a407d7c9f8041ddba2f4d28840eca923013fd8b0636b88b23`, then checks every provider payload against its manifest hash before reader admission. It compares all raw model and cost objects, exact lookup identities and encounter order across 42 shards and 1,592 models: 1,523 chat, 57 image and 12 classifier entries, with 13 API identifiers. It neither extracts files nor executes package code, requires no Node runtime, and does not call providers.

These tests qualify native retention/admission of the acquired catalog bytes and bounded authored cases. They do not call an upstream runtime catalog factory and do not establish live model availability, authentication, provider registration/replacement lifetimes, remote catalog overlays, monetary computation, transport or operation support. Full P2-05 and phase exit gates remain open.
