# Model catalog maintenance

All enabled provider adapters completed.

Upstream pinned catalog shards are unchanged. Missing and excluded models are retained. Prices are comparable only within identical currency, unit, tier, region and conditions.

- example-lab/future-protocol: added
- openrouter/example/atlas: updated
- openrouter/example/new-unpriced: added

| Provider/model | Dimension | Old | New | Absolute change | Percent | Source |
|---|---|---:|---:|---:|---:|---|
| example-lab/future-protocol | input / EUR per million-tokens / batch / eu-west / {} | unknown | 1.4 | n/a | n/a (unknown or zero baseline) | [official source](https://models.example.test/catalog) |
| example-lab/future-protocol | input / USD per million-tokens / standard / us-east / {} | unknown | 1.5 | n/a | n/a (unknown or zero baseline) | [official source](https://models.example.test/catalog) |
| openrouter/example/atlas | input / USD per million-tokens / catalog-override / unspecified / {"min_prompt_tokens":100000,"precedence":0} | unknown | 4 | n/a | n/a (unknown or zero baseline) | [official source](https://openrouter.ai/api/v1/models) |
| openrouter/example/atlas | cache-write-5m / USD per million-tokens / catalog-default / unspecified / {} | unknown | 2.5 | n/a | n/a (unknown or zero baseline) | [official source](https://openrouter.ai/api/v1/models) |
| openrouter/example/atlas | input / USD per million-tokens / catalog-default / unspecified / {} | 1 | 2 | 1 | 100% | [official source](https://openrouter.ai/api/v1/models) |
| openrouter/example/atlas | output / USD per million-tokens / catalog-default / unspecified / {} | 3 | 2 | -1 | -33.333333% | [official source](https://openrouter.ai/api/v1/models) |

## Review notes

- disabled-provider: skipped by configuration; existing entries preserved.
- example-lab/future-protocol: metadata retained; protocol or model type requires explicit runtime support review.
- excluded-provider: skipped by configuration; existing entries preserved.
- openrouter/example/atlas: one or more prices are unknown; prior known prices, if any, are retained.
- openrouter/example/new-unpriced: input/output pricing incomplete.
- openrouter/example/new-unpriced: one or more prices are unknown; prior known prices, if any, are retained.
- openrouter/example/legacy: absent or excluded; retained without deletion.

## Evidence

- [example-lab](https://models.example.test/catalog): SHA-256 `b638196c75251701642e5080b7e8fb8bfd979eaa81671416fc9179930bc4d21d`.
- [openrouter](https://openrouter.ai/api/v1/models): SHA-256 `1e6e032b777132ab1b78bf0fb68fdced40b582573dcef5b1b8606a37c14c42ea`.

Retrieval times and raw responses are in the run artifacts, outside the versioned overlay. No automatic merge is performed.
