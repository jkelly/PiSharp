// Offline structural checks only: these do not execute the mapped C# implementation.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { root, statusPath, buildStatus, validateStatus, attachCurrentMappings } from './gap-status-mapping.mjs';

const read = path => JSON.parse(readFileSync(resolve(root,path),'utf8'));

test('current mapping covers every audited correction, capability, verification and outstanding release row',()=>{
  const status=read(statusPath);
  validateStatus(status);
  assert.deepEqual(status,buildStatus());
  const register=read('AI_DOCS/upstream-audit-2026-11-08/gap-register.json');
  const ids=new Set(status.changes.map(row=>row.id));
  for(const row of register.findings) assert(ids.has(row.id),row.id);
  for(const id of [...Object.keys(register.capabilityPackages),...Object.keys(register.verificationPackages)]) assert(ids.has(id),id);
  const historical=read('compatibility/sync-1.5.0-applicability.json');
  assert.deepEqual(status.baselineRows.map(row=>row.id),historical.rows.filter(row=>['port','ported-with-deferral'].includes(row.status)).map(row=>row.id));
});

test('current pointers use actual paths and keep historical status separate',()=>{
  const status=read(statusPath);
  for(const [path,expected] of Object.entries(attachCurrentMappings(status))) assert.deepEqual(read(path),expected,path);
  const table=read('compatibility/sync-1.5.0-applicability.json');
  for(const row of status.baselineRows) {
    const historical=table.rows.find(value=>value.id===row.id);
    assert.equal(historical.status,row.historicalStatus);
    assert(historical.historicalPisharp);
    assert.deepEqual(historical.pisharp,row.sourcePaths);
    assert.equal(historical.currentImplementation.runtimeStatus,'pending-integrated-validation');
  }
  assert(status.platformAndEvidenceGates.some(value=>value.includes('Windows PTY')));
  assert(status.platformAndEvidenceGates.some(value=>value.includes('Cloudflare')));
  assert.equal(status.packaging.published,false);
});
