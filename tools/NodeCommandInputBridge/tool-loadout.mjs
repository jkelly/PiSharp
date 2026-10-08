// Pure metadata projection. No host callback, execute function or session context is supplied.
import assert from 'node:assert/strict';
const exact = (value, fields) => {
  assert(value && typeof value === 'object' && !Array.isArray(value));
  assert.deepEqual(Object.keys(value).sort(), fields.sort());
};
const text = (value, cap = 65536) => { assert(typeof value === 'string' && value.length <= cap); return value; };
const freeze = value => { if (value && typeof value === 'object') { Object.values(value).forEach(freeze); Object.freeze(value); } return value; };
export function readNamespace(value) {
  assert(value && typeof value === 'object' && !Array.isArray(value));
  exact(value, ['name', ...(Object.hasOwn(value, 'description') ? ['description'] : [])]);
  const result = { name: text(value.name) };
  if (Object.hasOwn(value, 'description')) result.description = text(value.description);
  return Object.freeze(result);
}
export function createToolLoadoutSnapshot(supplied) {
  // Sever caller ownership before exposing any object. The worker supplies strict parsed JSON.
  const value = JSON.parse(JSON.stringify(supplied));
  assert(Buffer.byteLength(JSON.stringify(value)) <= 262144, 'Loadout snapshot byte budget');
  exact(value, ['snapshotId', 'declared', 'callable', 'registered']);
  text(value.snapshotId, 128);
  assert(value.snapshotId.length > 0 && Array.isArray(value.registered) && value.registered.length <= 256);
  const byName = new Map(), namespaces = new Map(), exposures = new Map();
  const registered = value.registered.map(row => {
    exact(row, ['declaration', 'exposure', ...(Object.hasOwn(row, 'namespace') ? ['namespace'] : [])]);
    assert(row.declaration && typeof row.declaration === 'object' && !Array.isArray(row.declaration));
    const declaration = row.declaration;
    assert(Object.keys(declaration).every(key => ['name', 'label', 'description', 'parameters'].includes(key)), 'Unsupported loadout declaration field');
    text(declaration.name, 128); text(declaration.description);
    if (Object.hasOwn(declaration, 'label')) text(declaration.label);
    assert(declaration.name.length > 0 && declaration.parameters && typeof declaration.parameters === 'object' && !Array.isArray(declaration.parameters));
    assert(!byName.has(declaration.name), 'Duplicate registered tool');
    assert(['direct', 'model-only', 'codemode', 'deferred', 'hidden'].includes(row.exposure));
    const tool = freeze(declaration); byName.set(tool.name, tool); exposures.set(tool.name, row.exposure);
    if (Object.hasOwn(row, 'namespace')) namespaces.set(tool.name, readNamespace(row.namespace));
    return tool;
  });
  const ordered = names => {
    assert(Array.isArray(names) && names.length <= 256 && new Set(names).size === names.length);
    return Object.freeze(names.map(name => { text(name, 128); assert(byName.has(name), 'Unregistered loadout member'); return byName.get(name); }));
  };
  return Object.freeze({ declared: ordered(value.declared), callable: ordered(value.callable), registered: Object.freeze(registered),
    getExposure: name => exposures.get(name) ?? 'direct',
    getNamespace: name => namespaces.get(name) });
}
export function createToolLoadoutCache() {
  const snapshots = new Map(); let bytes = 0;
  return supplied => {
    const key = supplied.snapshotId, json = JSON.stringify(supplied);
    if (snapshots.has(key)) { const row = snapshots.get(key); assert.equal(row.json, json, 'Loadout snapshot identity changed'); return row.facade; }
    const length = Buffer.byteLength(json);
    assert(snapshots.size < 128 && bytes + length <= 8388608, 'Loadout retained snapshot budget');
    const facade = createToolLoadoutSnapshot(supplied);
    snapshots.set(key, { json, facade }); bytes += length; return facade;
  };
}
export async function invokeLoadoutPreparation(callback, loadout) {
  assert(typeof callback === 'function');
  const result = callback(loadout);
  if (result && typeof result.then === 'function') {
    try { await result; } catch { /* Join the original rejection before rejecting this unsupported shape. */ }
    assert.fail('Unsupported asynchronous prepareLoadout');
  }
  if (result === undefined || result === null) return result;
  assert(result && typeof result === 'object' && !Array.isArray(result));
  assert(Object.keys(result).every(key => ['descriptions', 'hiddenDeclarations'].includes(key)), 'Unsupported prepareLoadout changes');
  const changes = {};
  if (Object.hasOwn(result, 'descriptions')) {
    const descriptions = result.descriptions; assert(descriptions && typeof descriptions === 'object' && !Array.isArray(descriptions));
    assert(Object.keys(descriptions).length <= 256);
    changes.descriptions = Object.fromEntries(Object.entries(descriptions).map(([name, description]) => [text(name, 128), text(description)]));
  }
  if (Object.hasOwn(result, 'hiddenDeclarations')) {
    assert(Array.isArray(result.hiddenDeclarations) && result.hiddenDeclarations.length <= 256);
    changes.hiddenDeclarations = result.hiddenDeclarations.map(name => text(name, 128));
  }
  assert(Buffer.byteLength(JSON.stringify(changes)) <= 262144, 'Loadout changes byte budget');
  return freeze(changes);
}
