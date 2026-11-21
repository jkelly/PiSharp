// The v1.5.0..v1.5.2 sync's accepted table, its Durable overlay and its preserved Node fixture.
import { defineApplicabilityChecks } from './sync-applicability-checks.mjs';

defineApplicabilityChecks({
  tablePath: 'compatibility/sync-1.5.2-applicability.json',
  overlayPath: 'compatibility/durable-sync-1.5.2-overlay.json',
  preservedMapPath: 'compatibility/durable-port-map.v1.5.0.json',
  preservedMapSha256: '45fb24e89fb9d0c63ccc2fd8c05dd6e66f3b63fad0d7500f2964303380690ee2',
  fixtureRoot: 'tools/NodeBridge/runtime-fixture-152',
});
