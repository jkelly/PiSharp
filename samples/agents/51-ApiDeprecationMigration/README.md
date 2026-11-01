# API deprecation migration

Runs `inspect_migration` → `apply_migration` → `verify_migration` with explicit `migrationId` (`client-v2` in the fixture). The fixture provides allowlisted `repo/*.cs` source text and exact old/new usage strings with expected occurrence counts. Inspection reads existing workspace files when present; otherwise it uses the fixture source.

Only `apply_migration` writes source, and it requires both tool approval metadata and `SampleContext.Approved`. It checks all reviewed sources before writing, applies 1–20 bounded exact-text edits, and preserves every unrelated character. Sources and edited outputs are limited to 256 KiB. Changes to configuration or reviewed files block the operation. Repeated application verifies the prior result and is idempotent. Verification compares full expected final contents after all edits, so chained edits can replace intermediate usages; its artifact is `verification-<migrationId>.json`.

This is a local fixture migration, not a syntax-aware refactoring engine. Exact configured strings may also match comments; scope them precisely. It runs no code and makes no service calls. Multi-file writes are prevalidated but are not a filesystem transaction; cancellation/I/O failure can leave partial output, which must be inspected before retrying.

```powershell
dotnet run --project samples/agents/51-ApiDeprecationMigration -- --demo --approve
dotnet run --project samples/agents/51-ApiDeprecationMigration -- --self-test
```

Eight tests cover exact preservation, chained edits and final-content tampering, direct-callback approval, idempotence, stale-file protection, invalid counts, multiple IDs/files, path traversal, missing stages and changed configuration.
