// The v1.5.0..v1.5.2 sync, checked against its preserved lock (the current target has moved on; see sync-1.6.0-source.test.mjs).
import { defineSourceChecks } from './sync-source-checks.mjs';

defineSourceChecks({
  lockPath: 'compatibility/target.v1.5.2.lock.json',
  previousLockPath: 'compatibility/target.v1.5.0.lock.json',
  previousLockSha256: '429dad2debcf296a980cf3d813e7d1609855bc0d1e71f84c29bb003e758c3e1e',
  inventoryPath: 'compatibility/sync-1.5.2-files.json',
  inventoryLockPath: 'compatibility/target.v1.5.0.lock.json',
  tag: 'v1.5.2', commit: 'b4afdddf44e059c67ccf5097e5317d913521b470', files: 2531, version: '1.5.2',
  preservedRelease: { version: '1.5.0.1', tagCommit: '8144e3eaa138a5faf56f5cbb0de477a56fd48f8f', lockPath: 'compatibility/target.v1.5.0.lock.json' },
  upstreamEnv: 'PI_UPSTREAM_V1_5_2',
});
