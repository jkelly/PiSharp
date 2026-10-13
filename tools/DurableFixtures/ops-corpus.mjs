// The ops corpus capture.mjs writes to upstream-sessions/ops-corpus.json: seeded random edits made through the pinned Chord tracker
// (packages/chord/src/delta), each batch with the edits as portable commands, the operations the tracker emitted, and the resulting value.
// compat.ops.* replays every batch with PiSharp's DeltaApply, and makes the same edits through PiSharp's tracker.
//
// Edit commands; `path` names the container (an object or array) from the root, `key` an object key:
//   ["set", path, key|index, value]       container[key] = value (an array index may be one past the end)
//   ["delete", path, key]                 delete object[key]
//   ["append", path, key|index, text]     container[key] += text (a string)
//   ["trim", path, key|index, count]      container[key] = container[key].slice(count) (a string)
//   ["splice", path, start, remove, items] array.splice(start, remove, ...items)
//   ["move", path, from, to]              const [item] = array.splice(from, 1); array.splice(to, 0, item)
//   ["reverse", path]                     array.reverse()
//   ["sort", path]                        array.sort() (JavaScript's default order)
//   ["length", path, length]              array.length = length
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

export const SEED = 0x5eed1313;
const DOCUMENTS = 12;
const BATCHES = 10;

/** mulberry32: a small, well-mixed 32-bit generator. */
function random(seed) {
  let state = seed >>> 0;
  const next = () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  const int = (n) => Math.floor(next() * n);
  return { next, int, pick: (items) => items[int(items.length)] };
}

const WORDS = ['alpha', 'beta', 'gamma', 'delta', 'line\n', 'tab\t', 'quote"', 'é', '😀', 'x'.repeat(40), '', ' '];
const NUMBERS = [0, 1, -1, 42, 1.5, -0.25, 0.1, 1e21, 5e-7, 123456789012, 2 ** 53 - 1];

function scalar(r) {
  switch (r.int(5)) {
    case 0: return r.pick(WORDS) + r.pick(WORDS);
    case 1: return r.pick(NUMBERS);
    case 2: return r.next() < 0.5;
    case 3: return null;
    default: return `s${r.int(1000)}`;
  }
}

function value(r, depth) {
  const roll = r.int(depth >= 3 ? 1 : 4);
  if (roll === 0 || depth >= 3) return scalar(r);
  if (roll === 1) return Array.from({ length: r.int(5) }, () => value(r, depth + 1));
  const object = {};
  for (let index = r.int(5); index > 0; index--) object[`k${r.int(8)}`] = value(r, depth + 1);
  return object;
}

function base(r) {
  const root = { text: r.pick(WORDS) + 'start', items: [], nested: { log: '', list: [1, 2, 3] } };
  for (let index = r.int(4) + 2; index > 0; index--) root[`f${r.int(10)}`] = value(r, 1);
  for (let index = r.int(5) + 2; index > 0; index--) root.items.push(value(r, 2));
  return root;
}

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const clone = (v) => JSON.parse(JSON.stringify(v));

/** Every container in the value with its path. */
function containers(root) {
  const out = [];
  const walk = (node, path) => {
    out.push({ node, path });
    if (Array.isArray(node)) node.forEach((child, index) => { if (child !== null && typeof child === 'object') walk(child, [...path, index]); });
    else for (const [key, child] of Object.entries(node)) if (child !== null && typeof child === 'object') walk(child, [...path, key]);
  };
  walk(root, []);
  return out;
}

/** One random, valid edit command against the plain shadow value. */
function edit(r, shadow) {
  for (;;) {
    const { node, path } = r.pick(containers(shadow));
    const keys = Array.isArray(node) ? node.map((_, index) => index) : Object.keys(node);
    const strings = keys.filter((key) => typeof node[key] === 'string');
    const roll = r.int(10);
    if (roll === 0 && strings.length > 0) return ['append', path, r.pick(strings), r.pick(WORDS) + r.int(10)];
    if (roll === 1 && strings.length > 0) {
      const key = r.pick(strings);
      if (node[key].length > 0) return ['trim', path, key, 1 + r.int(node[key].length)];
      continue;
    }
    if (Array.isArray(node)) {
      switch (roll) {
        case 2: case 3: {
          const start = r.int(node.length + 1);
          return ['splice', path, start, r.int(node.length - start + 1), Array.from({ length: r.int(3) }, () => value(r, 2))];
        }
        case 4: if (node.length > 1) return ['move', path, r.int(node.length), r.int(node.length)]; continue;
        case 5: if (node.length > 1) return [r.next() < 0.5 ? 'reverse' : 'sort', path]; continue;
        case 6: return ['length', path, Math.max(0, node.length + r.int(4) - 2)];
        default: return ['set', path, r.int(node.length + 1), value(r, 2)];
      }
    }
    switch (roll) {
      case 2: case 3: if (keys.length > 0) return ['delete', path, r.pick(keys)]; continue;
      case 4: if (keys.length > 0) return ['set', path, r.pick(keys), value(r, 2)]; continue;
      default: return ['set', path, `k${r.int(12)}`, value(r, 2)];
    }
  }
}

/** Applies one edit command to a container reached from `root`: a plain value or a tracker draft. */
function perform(root, command) {
  const [verb, path] = command;
  let node = root;
  for (const segment of path) node = node[segment];
  switch (verb) {
    case 'set': node[command[2]] = clone(command[3]); break;
    case 'delete': delete node[command[2]]; break;
    case 'append': node[command[2]] += command[3]; break;
    case 'trim': node[command[2]] = node[command[2]].slice(command[3]); break;
    case 'splice': node.splice(command[2], command[3], ...clone(command[4])); break;
    case 'move': { const [item] = node.splice(command[2], 1); node.splice(command[3], 0, item); break; }
    case 'reverse': node.reverse(); break;
    case 'sort': node.sort(); break;
    case 'length': node.length = command[2]; break;
    default: throw new Error(`unknown edit ${verb}`);
  }
}

export async function opsCorpus(pi) {
  const { track, applyImmutable } = await import(pathToFileURL(join(pi, 'packages/chord/src/delta/index.ts')).href);
  const r = random(SEED);
  const documents = [];
  for (let d = 0; d < DOCUMENTS; d++) {
    const initial = base(r);
    const tracker = track(clone(initial));
    let shadow = clone(initial);
    const batches = [];
    for (let b = 0; b < BATCHES; b++) {
      const edits = [];
      for (let count = 1 + r.int(5); count > 0; count--) {
        const command = edit(r, shadow);
        perform(shadow, command);
        shadow = clone(shadow); // array.length growth leaves holes, which JSON (and the tracker) make explicit nulls
        edits.push(command);
      }
      const change = tracker.beginChange();
      for (const command of edits) perform(change.state, command);
      const prepared = change.prepare();
      const result = JSON.stringify(prepared.value);
      if (result !== JSON.stringify(shadow)) throw new Error(`document ${d} batch ${b}: the tracker disagrees with the plain edits`);
      if (JSON.stringify(applyImmutable(tracker.value, prepared.ops)) !== result) throw new Error(`document ${d} batch ${b}: replay differs`);
      tracker.adopt(prepared);
      shadow = JSON.parse(result);
      batches.push({ edits, ops: clone(prepared.ops), value: JSON.parse(result) });
    }
    documents.push({ base: initial, batches });
  }
  return { seed: SEED, generator: 'mulberry32', documents };
}
