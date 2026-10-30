# Maintained model catalog

`models.overlay.json` is an initially empty, separately versioned layer for reviewed provider metadata and prices. The [catalog maintainer](../samples/agents/31-ModelCatalogMaintainer/README.md) produces this format and opens update PRs without rewriting the upstream catalog shards or their pinned hashes.

PiSharp copies this file into its output directory. The model registry applies it after remote catalog refreshes and before user `models.json` and extension configuration. `PISHARP_MAINTAINED_CATALOG` selects an explicit alternative path. Registry rebuild/reload reads it again.

The schema is `{ "schemaVersion": 1, "providers": { "provider-id": { "model-id": { "metadata": {}, "prices": [] } } } }`. The sample's README documents the full provenance and price dimension contract. `PiSharp.AI.Catalogs.MaintainedCatalogOverlay.Apply` is the pure public reader used by the CLI.

Only complete chat entries with an existing provider transport are admitted. Existing routes and compatibility metadata are preserved. Runtime price changes require unambiguous USD-per-million-token rates with no regional/time/tier conditions. Missing values are retained for existing models and reject incomplete new models. Unknown prices never become free prices. Unsupported or conditional data remains in the source evidence and produces diagnostics until a reviewed native mapping is added.

This file is not a credential store. The maintainer's retrieval logs and transient timestamps stay outside versioned pricing data.
