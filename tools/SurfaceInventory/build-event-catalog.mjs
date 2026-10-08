#!/usr/bin/env node
// Builds the pinned extension event catalog for the upstream ref named by a lock file.
//
// The catalog's authored semantics (reducer family, failure policy, context rules, native profile)
// come from a template catalog. Every upstream source span (event declaration, ExtensionAPI.on
// subscription, runner dispatcher), the whole-file source pins and the event-bus members are
// re-derived from the upstream sources with the same census() used for the P6-01 native-surface
// inventory. An optional overlay records native status changes authored for the target.
//
// Reproduction: with the v0.99.1 lock and the v0.99.1 catalog as template (and no overlay), the
// output is byte-for-byte compatibility/extensions/event-catalog.json.
//
// Usage:
//   node tools/SurfaceInventory/build-event-catalog.mjs --upstream <git checkout containing the ref>
//     --lock compatibility/target.lock.json --template compatibility/extensions/event-catalog.json
//     [--overlay compatibility/extensions/event-catalog.v1.1.0.overlay.json]
//     (--out <new catalog> | --check <existing catalog>)
//
// Reads upstream bytes with `git show <commit>:<path>` (canonical Git blob bytes, no CRLF
// conversion). Never writes to the upstream checkout and never runs upstream code.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { census } from './native-extension-surface.test.mjs';

export const EXT = 'packages/coding-agent/src/core/extensions/';
export const TYPES = EXT + 'types.ts', RUNNER = EXT + 'runner.ts', LOADER = EXT + 'loader.ts';
export const CACHE_WARMER = 'packages/coding-agent/src/core/cache-warmer.ts', EVENT_BUS = 'packages/coding-agent/src/core/event-bus.ts';
const PINNED_SOURCES = [TYPES, RUNNER, LOADER, CACHE_WARMER];
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');

export const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const gitBlob = bytes => createHash('sha1').update(Buffer.concat([Buffer.from('blob ' + bytes.length + '\0'), bytes])).digest('hex');
const lineAt = (text, offset) => text.slice(0, offset).split('\n').length;
export function sourceRef(path, text, start, end) {
  return { path, startLine: lineAt(text, start), endLine: lineAt(text, end - 1), startUtf16: start, endUtf16: end, sha256: sha256(Buffer.from(text.slice(start, end))) };
}
// Same anchor rule as methodRef in native-extension-surface.test.mjs: the method starts at its
// declaration line and stops at the next peer method or top-level function declaration.
export function methodRef(path, text, name) {
  const match = new RegExp('(^|\\n)(?:export (?:async )?function |\\t(?:private )?(?:async )?)' + name + '(?:<[^\\n]*>)?\\(').exec(text);
  assert(match, 'Missing source method ' + name); const start = match.index + (match[1] ? 1 : 0);
  const lines = text.slice(start).split('\n'); let count = lines.length;
  for (let i = 1; i < lines.length; i++) if (/^(?:export (?:async )?function |\t(?:private )?(?:async )?)[A-Za-z_$][\w$]*(?:<[^\n]*>)?\(/.test(lines[i])) { count = i; break; }
  const end = start + lines.slice(0, count).join('\n').length;
  return { declaration: name, ...sourceRef(path, text, start, end) };
}
const sourcePin = (commit, path, bytes) => ({ path, bytes: bytes.length, sha256: sha256(bytes), gitBlob: gitBlob(bytes), sourceUrl: 'https://github.com/earendil-works/pi/blob/' + commit + '/' + path });

// Derives every source-linked field of the catalog from upstream bytes. `read(path)` returns a Buffer.
export function deriveSources(read, commit, dispatcherMethods) {
  const text = path => read(path).toString('utf8');
  const types = census(text(TYPES), TYPES);
  const api = types.declarations.find(row => row.name === 'ExtensionAPI'); assert(api, 'ExtensionAPI missing');
  const events = api.members.filter(row => row.name === 'on').map(row => {
    const name = row.signature.match(/event:\s*"([^"]+)"/)?.[1]; assert(name, 'Unnamed ExtensionAPI.on overload ' + row.id);
    const handler = row.signature.match(/handler:\s*([\s\S]*?)\s*\)\s*:\s*\(\)\s*=>\s*void;/)?.[1]?.replace(/,\s*$/, ''); assert(handler, 'Handler shape ' + row.id);
    const eventType = name === 'project_trust' ? 'ProjectTrustEvent' : handler.match(/^ExtensionHandler<([A-Za-z]+Event)/)?.[1]; assert(eventType, 'Event type ' + row.id);
    let eventDeclaration = types.declarations.find(declaration => declaration.name === eventType)?.source;
    if (!eventDeclaration) {
      assert.equal(eventType, 'CacheWarmingDecisionEvent', 'Event declaration not found: ' + eventType);
      const body = text(CACHE_WARMER), start = body.indexOf('export interface ' + eventType), end = body.indexOf('\n}', start) + 2;
      assert(start >= 0 && end > start); eventDeclaration = sourceRef(CACHE_WARMER, body, start, end);
    }
    const method = dispatcherMethods(name);
    return { name, eventType, subscriptionMemberId: row.id, signature: row.signature, subscription: row.source, eventDeclaration, dispatcher: method ? methodRef(RUNNER, text(RUNNER), method) : null };
  });
  const contexts = Object.fromEntries(['ExtensionContext', 'ProjectTrustContext', 'ExtensionCommandContext', 'ExtensionToolContext'].map(name => {
    const row = types.declarations.find(declaration => declaration.name === name); assert(row, name + ' missing');
    return [name, row.members.map(member => member.name)];
  }));
  const bus = census(text(EVENT_BUS), EVENT_BUS).declarations.filter(row => row.name === 'EventBus').flatMap(row => row.members);
  return {
    events, contexts, busMembers: bus,
    sourceFiles: PINNED_SOURCES.map(path => sourcePin(commit, path, read(path))),
    busSource: sourcePin(commit, EVENT_BUS, read(EVENT_BUS)),
  };
}

const sortedEqual = (left, right) => JSON.stringify([...left].sort()) === JSON.stringify([...right].sort());
function sourceOperations(row) {
  return row.name === 'project_trust' ? null : row.context.sourceOperations;
}
function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value && typeof value === 'object' && !Array.isArray(value) && target[key] && typeof target[key] === 'object' && !Array.isArray(target[key])) merge(target[key], value);
    else target[key] = structuredClone(value);
  }
  return target;
}

// Builds the catalog for `commit` from the template, re-deriving all source evidence.
export function buildCatalog({ template, read, commit, tag, overlay = null, templatePath = null, templateSha256 = null }) {
  const byName = new Map(template.events.map(row => [row.name, row]));
  const derived = deriveSources(read, commit, name => byName.get(name)?.source.dispatcher?.declaration ?? null);
  const same = commit === template.sourceSha;
  // Context operation sets are authored per row in the template; a source change must be re-authored.
  const expected = { ExtensionContext: byName.get('session_start').context.sourceOperations, ExtensionCommandContext: byName.get('session_start').context.commandOnlyOperationsExcluded, ExtensionToolContext: byName.get('session_start').context.toolOnlyOperationsExcluded };
  for (const [name, members] of Object.entries(expected)) assert(sortedEqual(members, derived.contexts[name]), name + ' members changed upstream; re-author context operations before refreshing');
  assert(sortedEqual(derived.contexts.ProjectTrustContext, ['cwd', 'mode', 'hasUI', 'ui']), 'ProjectTrustContext members changed upstream');

  const added = derived.events.filter(row => !byName.has(row.name)).map(row => row.name);
  const removed = template.events.filter(row => !derived.events.some(event => event.name === row.name)).map(row => row.name);
  if (added.length) throw new Error('New upstream events need authored catalog semantics first: ' + added.join(', '));
  const changes = { eventDeclaration: [], subscription: [], dispatcher: [] };
  const events = derived.events.map(source => {
    const previous = byName.get(source.name), row = structuredClone(previous);
    assert.equal(row.eventType, source.eventType, 'Event type changed for ' + source.name);
    assert(sourceOperations(row) === null || sortedEqual(sourceOperations(row), derived.contexts.ExtensionContext));
    const was = previous.source;
    row.source = { eventDeclaration: source.eventDeclaration, subscription: source.subscription, subscriptionMemberId: source.subscriptionMemberId, signature: source.signature, dispatcher: source.dispatcher };
    if (!same) {
      const changed = {
        eventDeclaration: was.eventDeclaration.sha256 !== source.eventDeclaration.sha256,
        subscription: was.subscription.sha256 !== source.subscription.sha256 || was.subscriptionMemberId !== source.subscriptionMemberId,
        dispatcher: was.dispatcher.sha256 !== source.dispatcher.sha256,
      };
      for (const [key, value] of Object.entries(changed)) if (value) changes[key].push(source.name);
      row.sourceRefresh = {
        previousSourceSha: template.sourceSha,
        eventDeclarationTextChanged: changed.eventDeclaration, subscriptionTextChanged: changed.subscription, dispatcherTextChanged: changed.dispatcher,
        previous: { eventDeclarationSha256: was.eventDeclaration.sha256, subscriptionSha256: was.subscription.sha256, dispatcherSha256: was.dispatcher.sha256 },
        removedUpstream: false,
      };
    }
    return row;
  });
  for (const name of removed) {
    // A removed upstream event stays listed and is marked rather than dropped.
    const row = structuredClone(byName.get(name));
    row.sourceRefresh = { previousSourceSha: template.sourceSha, removedUpstream: true, note: 'ExtensionAPI.on overload absent at ' + commit + '; spans retained from ' + template.sourceSha + '.' };
    events.push(row);
  }

  const catalog = {};
  for (const [key, value] of Object.entries(template)) {
    catalog[key] = structuredClone(value);
    if (key === 'sourceSha') {
      catalog.sourceSha = commit;
      if (!same) {
        catalog.sourceTag = tag;
        catalog.refresh = {
          previousCatalog: { path: templatePath, sha256: templateSha256, sourceSha: template.sourceSha },
          generator: 'tools/SurfaceInventory/build-event-catalog.mjs',
          method: 'Row set and authored per-family semantics carried from the previous catalog; every event declaration, ExtensionAPI.on subscription and runner dispatcher span, the whole-file source pins and the event-bus members re-derived from canonical Git blob bytes at the target commit with the census() of the P6-01 inventory.',
          summary: { events: events.length, added, removed, eventDeclarationTextChanged: changes.eventDeclaration, subscriptionTextChanged: changes.subscription, dispatcherTextChanged: changes.dispatcher },
        };
      }
    }
  }
  catalog.authority.requiredOverloads = derived.events.length;
  catalog.authority.sourceFiles = derived.sourceFiles;
  if (!same) {
    catalog.authority.inventoryBaseline = template.sourceSha;
    catalog.authority.inventoryRelation = 'The P6-01 native-surface inventory above is preserved ' + template.sourceSha.slice(0, 7) + ' evidence and was not refreshed. At ' + commit.slice(0, 7) + ' the ExtensionAPI.on overloads were re-censused directly from the pinned sources.';
  }
  catalog.events = events;
  catalog.gates.mandatoryEvents = events.length;
  const bus = catalog.dynamicEventBus, templateMembers = new Map(template.dynamicEventBus.members.map(row => [row.name, row]));
  bus.sourceEvidence = derived.busSource;
  bus.members = derived.busMembers.map(member => {
    const authored = templateMembers.get(member.name); assert(authored, 'New event-bus member needs authored behavior: ' + member.name);
    return { ...member, id: 'ExtensionAPI.events.' + member.name, required: true, nativeBehavior: authored.nativeBehavior, owner: authored.owner, behavior: authored.behavior };
  });
  if (overlay) applyOverlay(catalog, overlay);
  return catalog;
}

export function applyOverlay(catalog, overlay) {
  for (const [key, value] of Object.entries(overlay)) {
    if (key === 'events') {
      for (const [name, patch] of Object.entries(value)) {
        const row = catalog.events.find(event => event.name === name); assert(row, 'Overlay names unknown event ' + name);
        merge(row, patch);
      }
    } else if (key !== '$comment') {
      catalog[key] = catalog[key] && typeof catalog[key] === 'object' && !Array.isArray(catalog[key]) ? merge(catalog[key], value) : structuredClone(value);
    }
  }
  return catalog;
}

export const serialize = catalog => JSON.stringify(catalog, null, 2) + '\n';

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 2) { assert(argv[i].startsWith('--'), 'Unexpected argument ' + argv[i]); args[argv[i].slice(2)] = argv[i + 1]; }
  for (const name of ['upstream', 'lock', 'template']) assert(args[name], '--' + name + ' is required');
  assert(!!args.out !== !!args.check, 'Pass exactly one of --out or --check');
  return args;
}

export function gitReader(upstream, commit) {
  const git = (...command) => execFileSync('git', ['-C', upstream, ...command], { maxBuffer: 1 << 30 });
  assert.equal(git('rev-parse', commit + '^{commit}').toString().trim(), commit, 'Upstream checkout lacks ' + commit);
  const cache = new Map();
  return path => { if (!cache.has(path)) cache.set(path, git('show', commit + ':' + path)); return cache.get(path); };
}

export function main(argv = process.argv.slice(2)) {
  const args = parseArgs(argv);
  const lock = JSON.parse(readFileSync(resolve(repo, args.lock), 'utf8'));
  const commit = lock.source.commit, tag = lock.source.tag;
  const read = gitReader(resolve(args.upstream), commit);
  // Whole-file pins recorded in the lock must match the bytes read.
  for (const path of [...PINNED_SOURCES, EVENT_BUS]) {
    const pin = lock.artifacts.find(row => row.kind === 'source-file' && row.path === path);
    if (pin) assert.equal(sha256(read(path)), pin.sha256, path + ' differs from ' + args.lock);
  }
  const templateBytes = readFileSync(resolve(repo, args.template));
  const overlay = args.overlay ? JSON.parse(readFileSync(resolve(repo, args.overlay), 'utf8')) : null;
  const catalog = buildCatalog({ template: JSON.parse(templateBytes.toString('utf8')), read, commit, tag, overlay, templatePath: args.template.replaceAll('\\', '/'), templateSha256: sha256(templateBytes) });
  const bytes = Buffer.from(serialize(catalog));
  if (args.out) { writeFileSync(resolve(repo, args.out), bytes); process.stdout.write(JSON.stringify({ wrote: args.out, sha256: sha256(bytes), events: catalog.events.length, refresh: catalog.refresh?.summary ?? null }, null, 2) + '\n'); return; }
  const target = resolve(repo, args.check); assert(existsSync(target), args.check + ' missing');
  const existing = readFileSync(target);
  if (!existing.equals(bytes)) { process.stderr.write(args.check + ' does not match the generated catalog\n'); process.exitCode = 1; return; }
  process.stdout.write(JSON.stringify({ reproduced: args.check, sha256: sha256(bytes), events: catalog.events.length }, null, 2) + '\n');
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
