import { defineSourceChecks } from './sync-source-checks.mjs';

defineSourceChecks({
  lockPath: 'compatibility/target.lock.json',
  previousLockPath: 'compatibility/target.v1.6.0.lock.json',
  previousLockSha256: '14f2d8111180f457616cd5041168f11d5991d9d6eadf6008256e409a3b0eaacd',
  inventoryPath: 'compatibility/sync-1.7.0-files.json',
  inventoryLockPath: 'compatibility/target.v1.5.0.lock.json',
  tag: 'v1.7.0', commit: 'fb0fe823539f6d04850f6e34748c01f01f212e1f', files: 1683, version: '1.7.0',
  preservedRelease: { version: '1.6.0.1', tagCommit: '1a74a207b30613444d5f8ef884957e7580860539', lockPath: 'compatibility/target.v1.6.0.lock.json' },
  upstreamEnv: 'PI_UPSTREAM_V1_7_0',
});
