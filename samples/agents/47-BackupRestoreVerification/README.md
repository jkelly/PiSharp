# Backup Restore Verification

Creates actual fixture source files, copies local snapshots, restores to isolated directories, and compares SHA-256 digests of the restored bytes against the recorded manifest. No production files or backup services are accessed.

```powershell
dotnet run --project samples/agents/47-BackupRestoreVerification -- --demo --approve
dotnet run --project samples/agents/47-BackupRestoreVerification -- --self-test
```

Call `plan_backup`, `create_snapshot`, then `verify_restore` with an explicit `datasetId`. Creation and restore require `--approve`, with callback-level checks. The orders fixture contains two real text files. Paths stay within the selected workspace under `sources/orders`, `snapshots/orders` and `restores/orders/attempt-N`. Each verification gets a fresh restore directory, avoiding accidental reuse of old restored files. `backup-manifest-orders.json` and `restore-verification-orders.json` retain expected/actual hashes and verified, missing or corrupt status.

File inventories are bounded to 1–50 small fixture files; traversal, duplicates and missing content are rejected. Existing source files are preserved by refusing overwrite. Within one run, repeated snapshot creation returns its original receipt and does not repair a corrupted snapshot. Changed dataset content is rejected after snapshot creation. Cross-process resume is intentionally not implemented; use a fresh workspace for a new demo.

Ten focused cases exercise real restore bytes, snapshot idempotency, isolated repeated restores, distinct datasets, corruption, missing snapshot files, approval, missing prerequisites/data, traversal, overwrite refusal and changed input. No credentials or network access are required.
