// The v1.5.2..v1.6.0 sync: the current target lock, the preserved v1.5.2 lock and the complete file inventory.
// When a later sync moves the target, point lockPath at this sync's preserved copy (compatibility/target.v1.6.0.lock.json).
import { defineSourceChecks } from './sync-source-checks.mjs';

defineSourceChecks({
  lockPath: 'compatibility/target.lock.json',
  previousLockPath: 'compatibility/target.v1.5.2.lock.json',
  previousLockSha256: '5934c0481fd2c908ada840dfc33d1f9a69b11b03d1848d7080edf9116782eb66',
  inventoryPath: 'compatibility/sync-1.6.0-files.json',
  inventoryLockPath: 'compatibility/target.v1.5.0.lock.json',
  tag: 'v1.6.0', commit: 'faac585884ae2c013a6b1400f3e9e77f10b34bac', files: 1311, version: '1.6.0',
  preservedRelease: { version: '1.5.2', tagCommit: 'f786a55dc32d1767ac220ed1ad4c44748bccba97', lockPath: 'compatibility/target.v1.5.2.lock.json' },
  upstreamEnv: 'PI_UPSTREAM_V1_6_0',
});
