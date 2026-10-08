#!/usr/bin/env node
// Builds the coding-agent command/RPC/SDK declaration inventory for the upstream ref named by a lock.
//
// The original v0.99.1 inventory was authored by read-only source inspection, without a committed
// generator. This tool carries its authored row text forward and re-derives every source-linked
// field at the target ref:
//   - whole-file pins (Git blob ID, SHA-256, bytes; checkout bytes when the checkout is at the ref);
//   - every source anchor (row source plus dispatch/response/forwarding/projection/augmentation
//     sources): files that are byte-identical keep their anchors; anchors in changed files are
//     relocated through a line-level LCS mapping, union members through a brace census, and parser
//     branches are bounded at the next `} else if`;
//   - embedded declaration text (exact or trimmed span text, package.json export objects);
//   - completeness counts.
// Censuses of the parser branches, SDK root exports, module exports, package entries, session and
// assistant event unions detect added and removed surfaces. Added SDK root exports and parser flags
// get rows cloned from a sibling of the same kind; other additions stop the build for authoring.
// Removed surfaces are kept and marked. An optional overlay records native status.
//
// Reproduction: with the v0.99.1 lock and the v0.99.1 inventory as template (and no overlay), the
// output is byte-for-byte compatibility/coding-agent-command-rpc-inventory.json.
//
// Usage:
//   node tools/SurfaceInventory/build-command-rpc-inventory.mjs --upstream <git checkout with both refs>
//     --lock compatibility/target.lock.json --template compatibility/coding-agent-command-rpc-inventory.json
//     [--overlay <native status overlay>] (--out <new inventory> | --check <existing inventory>)
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { gitReader, sha256 } from './build-event-catalog.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const ARGS = 'packages/coding-agent/src/cli/args.ts', INDEX = 'packages/coding-agent/src/index.ts', PACKAGE = 'packages/coding-agent/package.json';
const AGENT_TYPES = 'packages/agent/src/types.ts', SESSION = 'packages/coding-agent/src/core/agent-session.ts', AI_TYPES = 'packages/ai/src/types.ts';
const SOURCE_REF_KEYS = ['source', 'dispatchSource', 'responseSource', 'forwardingSource', 'wireProjectionSource', 'augmentationSource', 'projectionSource'];
const TRIMMED = new Set(['built-in-parser-flag', 'rpc-extension-ui-adapter-member']);
// The one v0.99.1 anchor whose recorded endLine is the line after its final newline (the
// "newline-boundary" labels were corrected everywhere else). Kept verbatim while its file is unchanged.
const KNOWN_LINE_LABELS = new Set(['rpc.command.get_commands.dispatchSource']);

const gitBlob = bytes => createHash('sha1').update(Buffer.concat([Buffer.from('blob ' + bytes.length + '\0'), bytes])).digest('hex');
const lineAt = (text, offset) => text.slice(0, offset).split('\n').length;
const urlFor = (commit, path, line) => 'https://github.com/earendil-works/pi/blob/' + commit + '/' + path + (line ? '#L' + line : '');

// ---------------------------------------------------------------- line mapping
function lineStarts(text) { const starts = [0]; for (let i = 0; i < text.length; i++) if (text[i] === '\n') starts.push(i + 1); return starts; }
// Maps each old line index to its new line index (or -1) through a longest common subsequence.
export function mapLines(oldText, newText) {
  const a = oldText.split('\n'), b = newText.split('\n'), map = new Int32Array(a.length).fill(-1);
  let prefix = 0; while (prefix < a.length && prefix < b.length && a[prefix] === b[prefix]) { map[prefix] = prefix; prefix++; }
  let suffix = 0; while (suffix < a.length - prefix && suffix < b.length - prefix && a[a.length - 1 - suffix] === b[b.length - 1 - suffix]) { map[a.length - 1 - suffix] = b.length - 1 - suffix; suffix++; }
  const n = a.length - prefix - suffix, m = b.length - prefix - suffix;
  if (n && m) {
    const width = m + 1, table = new Uint16Array((n + 1) * width);
    for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--)
      table[i * width + j] = a[prefix + i] === b[prefix + j] ? table[(i + 1) * width + j + 1] + 1 : Math.max(table[(i + 1) * width + j], table[i * width + j + 1]);
    let i = 0, j = 0;
    while (i < n && j < m) {
      if (a[prefix + i] === b[prefix + j]) { map[prefix + i] = prefix + j; i++; j++; }
      else if (table[(i + 1) * width + j] >= table[i * width + j + 1]) i++; else j++;
    }
  }
  return map;
}
function relocator(oldText, newText) {
  const same = oldText === newText, oldStarts = lineStarts(oldText), newStarts = lineStarts(newText), map = same ? null : mapLines(oldText, newText);
  const lineIndex = offset => { let lo = 0, hi = oldStarts.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (oldStarts[mid] <= offset) lo = mid; else hi = mid - 1; } return lo; };
  const offset = position => {
    if (same) return position;
    const line = lineIndex(position), target = map[line];
    return target < 0 ? -1 : newStarts[target] + (position - oldStarts[line]);
  };
  return { same, offset };
}

// ---------------------------------------------------------------- censuses
function skipTrivia(text, i) {
  for (;;) {
    while (i < text.length && /\s/.test(text[i])) i++;
    if (text.startsWith('//', i)) { const end = text.indexOf('\n', i); i = end < 0 ? text.length : end; continue; }
    if (text.startsWith('/*', i)) { i = text.indexOf('*/', i + 2) + 2; continue; }
    return i;
  }
}
function matchBrace(text, open) {
  let depth = 0, quote = null;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
    if (c === '"' || c === "'" || c === '`') { quote = c; continue; }
    if (text.startsWith('//', i)) { i = text.indexOf('\n', i); continue; }
    if (text.startsWith('/*', i)) { i = text.indexOf('*/', i + 2) + 1; continue; }
    if (c === '{') depth++; else if (c === '}' && --depth === 0) return i + 1;
  }
  throw new Error('Unbalanced brace at ' + open);
}
// Top-level `{ ... }` members of `export type <name> =`, with their discriminator.
export function unionMembers(text, name) {
  const head = 'export type ' + name + ' =', start = text.indexOf(head); assert(start >= 0, 'Missing union ' + name);
  const members = []; let i = start + head.length, angle = 0;
  for (;;) {
    i = skipTrivia(text, i); const c = text[i];
    if (c === ';' && angle === 0) break;
    if (c === '<') angle++; else if (c === '>') angle--;
    if (c === '{' && angle === 0) {
      const end = matchBrace(text, i), body = text.slice(i, end), type = body.match(/^\{\s*type:\s*"([^"]+)"/)?.[1];
      assert(type, 'Union member without leading discriminator in ' + name);
      members.push({ type, start: i, end }); i = end; continue;
    }
    if (c === '{') { i = matchBrace(text, i); continue; }
    i++;
  }
  return members;
}
// Parser spellings by branch: each `arg === "<spelling>"` group inside parseArgs.
export function parserBranches(text) {
  const start = text.indexOf('export function parseArgs'), end = text.indexOf('\n}\n', start); assert(start >= 0 && end > start);
  const body = text.slice(start, end), branches = [];
  for (const match of body.matchAll(/(?:\n\t\t)(?:\} else )?if \(([^\n]*)\) \{/g)) {
    const spellings = [...match[1].matchAll(/arg === "([^"]+)"/g)].map(row => row[1]);
    if (spellings.length) branches.push({ spellings, start: start + match.index + (match[0].startsWith('\n\t\t}') ? 3 : 0) });
  }
  return branches;
}
// SDK root named exports: `export { a, type B } from "x"` and `export type { ... } from "x"`.
export function rootExports(text) {
  const result = [];
  for (const match of text.matchAll(/^export (type )?\{([\s\S]*?)\} from "([^"]+)";/gm)) {
    const blockType = !!match[1], bodyStart = match.index + match[0].indexOf('{') + 1;
    // Comments inside the clause are blanked with spaces so offsets stay exact.
    const clause = match[2].replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\//g, comment => ' '.repeat(comment.length));
    for (const item of clause.matchAll(/(type\s+)?([A-Za-z_$][\w$]*)(?:\s+as\s+([A-Za-z_$][\w$]*))?/g)) {
      const typeOnly = blockType || !!item[1], start = bodyStart + item.index;
      result.push({ name: item[3] ?? item[2], originalSymbol: item[2], typeOnly, origin: match[3], start, end: start + item[0].length });
    }
  }
  assert(!/^export \*/m.test(text), 'Wildcard root export needs authoring');
  return result;
}
export function moduleExports(text) {
  return [...text.matchAll(/^export (?:async )?(?:function|const|type|interface|class) ([A-Za-z_$][\w$]*)/gm)].map(row => row[1]);
}
function helpSubcommands(text) { return [...text.matchAll(/^\s*\$\{APP_NAME\} (install|remove|uninstall|update|list|config|auth|mcp)\b/gm)].map(row => row[1]); }

// ---------------------------------------------------------------- builder
function setRefFields(ref, commit, bytes, location) {
  ref.sha256 = sha256(bytes); ref.gitBlobId = gitBlob(bytes); ref.location = location; ref.url = urlFor(commit, ref.path, location.startLine);
}
function locate(text, start, end) { return { startLine: lineAt(text, start), endLine: lineAt(text, end - 1), startOffset: start, endOffset: end }; }

export function buildInventory({ template, readOld, readNew, commit, tree, tag, createdAt, checkout, overlay = null, templatePath = null, templateSha256 = null, cleanCheckout = null }) {
  const same = commit === template.sourceSha, baselineId = same ? template.baselineId : 'pi-' + tag + '@' + commit;
  const oldText = path => readOld(path).toString('utf8'), newText = path => readNew(path).toString('utf8');
  const relocators = new Map(), relocate = path => { if (!relocators.has(path)) relocators.set(path, relocator(oldText(path), newText(path))); return relocators.get(path); };
  const changes = { relocatedText: [], unionCensus: [], removed: [], added: [] };

  // Censuses at both refs; the old census must describe the template exactly.
  const censusAt = text => ({
    flags: parserBranches(text(ARGS)), root: rootExports(text(INDEX)), modules: moduleExports(text(ARGS)), help: helpSubcommands(text(ARGS)),
    entries: Object.keys(JSON.parse(text(PACKAGE)).exports),
    agentEvents: unionMembers(text(AGENT_TYPES), 'AgentEvent'), sessionEvents: unionMembers(text(SESSION), 'AgentSessionEvent'), assistantEvents: unionMembers(text(AI_TYPES), 'AssistantMessageEvent'),
  });
  const before = censusAt(oldText), after = censusAt(newText);
  const rowsOf = kind => template.rows.filter(row => row.classification === kind);
  const flagRows = rowsOf('built-in-parser-flag');
  assert.deepEqual(before.flags.map(row => row.spellings.join('|')), flagRows.map(row => [row.name, ...row.aliases].join('|')), 'Parser census differs from template');
  const rootRows = template.rows.filter(row => row.classification.startsWith('public-root-'));
  assert.deepEqual(before.root.map(row => [row.name, row.typeOnly, row.origin, row.start, row.end].join()), rootRows.map(row => [row.name, row.typeOnly, row.exportOrigin, row.source.location.startOffset, row.source.location.endOffset].join()), 'Root export census differs from template');
  assert.deepEqual([...new Set(before.help)], rowsOf('help-declared-subcommand').map(row => row.name), 'Help subcommand census differs');
  assert.deepEqual(before.entries, [...rowsOf('published-root-entry-declaration'), ...rowsOf('separate-package-subpath-entry-declaration')].map(row => row.name), 'Package entry census differs');
  const eventNames = census => [...census.agentEvents.filter(row => row.type !== 'agent_end'), ...census.sessionEvents].map(row => row.type);
  assert.deepEqual(eventNames(before), rowsOf('rpc-json-session-event').map(row => row.name.split('.')[0]), 'Session event census differs');
  assert.deepEqual(before.assistantEvents.map(row => row.type), rowsOf('nested-assistant-event-declaration').map(row => row.name.split('.').pop()), 'Assistant event census differs');
  const argsModuleRows = template.rows.filter(row => row.classification.startsWith('module-') && row.source.path === ARGS).map(row => row.name);
  assert.deepEqual(before.modules, argsModuleRows, 'args.ts module export census differs');
  for (const [key, label] of [['help', 'help subcommand'], ['entries', 'package entry'], ['modules', 'args.ts module export']])
    assert.deepEqual(after[key], before[key], label + ' set changed upstream; author rows before refreshing');
  assert.deepEqual(after.assistantEvents.map(row => row.type), before.assistantEvents.map(row => row.type), 'Assistant event union changed upstream');
  assert.deepEqual(eventNames(after), eventNames(before), 'Session event union changed upstream');

  // Relocates one source anchor; returns the new location.
  const anchor = (row, key, ref) => {
    const where = row.id + '.' + key, oldBody = oldText(ref.path), body = newText(ref.path), { same: identical, offset } = relocate(ref.path), loc = ref.location;
    const recorded = locate(oldBody, loc.startOffset, loc.endOffset);
    if (!KNOWN_LINE_LABELS.has(where)) assert.deepEqual(loc, recorded, 'Recorded location disagrees with its offsets: ' + where);
    if (identical) return structuredClone(loc);
    let start = offset(loc.startOffset), end = offset(loc.endOffset);
    // Union members are re-anchored by discriminator census when their own lines changed.
    const unionKey = key === 'source' && (row.classification === 'rpc-json-session-event' || row.classification === 'nested-assistant-event-declaration');
    if (unionKey) {
      const union = ref.path === AGENT_TYPES ? 'agentEvents' : ref.path === SESSION ? 'sessionEvents' : 'assistantEvents';
      const index = before[union].findIndex(member => member.start === loc.startOffset && member.end === loc.endOffset); assert(index >= 0, 'Union member not in census: ' + where);
      const member = after[union][index]; assert.equal(member.type, before[union][index].type);
      if (start !== member.start || end !== member.end) changes.unionCensus.push(where);
      start = member.start; end = member.end;
    }
    if (key === 'source' && row.classification.startsWith('public-root-')) {
      const member = after.root.find(item => item.name === row.name); assert(member, 'Removed root export reached anchor: ' + where);
      start = member.start; end = member.end;
    }
    if (key === 'source' && row.classification === 'built-in-parser-flag') {
      const branch = after.flags.find(item => item.spellings[0] === row.name); assert(branch, 'Parser branch missing: ' + where);
      if (row.name !== '--') assert.equal(start, branch.start, 'Parser branch start moved: ' + where);
      const next = after.flags.find(item => item.start > branch.start);
      if (next && end > next.start) end = row.name === '--' ? next.start : next.start; // a branch ends where the next begins
    }
    assert(start >= 0 && end >= 0 && end > start, 'Anchor could not be relocated: ' + where);
    if (body.slice(start, end) !== oldBody.slice(loc.startOffset, loc.endOffset)) changes.relocatedText.push(where);
    return locate(body, start, end);
  };

  const finishRow = (row, original) => {
    row.baselineId = baselineId;
    for (const key of SOURCE_REF_KEYS) {
      const ref = row[key]; if (!ref) continue;
      const location = original ? anchor(original, key, original[key]) : ref.location;
      setRefFields(ref, commit, readNew(ref.path), location);
    }
    const span = newText(row.source.path).slice(row.source.location.startOffset, row.source.location.endOffset);
    if ('sourceBranch' in row) row.sourceBranch = span.trim();
    if ('sourceDeclaration' in row) row.sourceDeclaration = span;
    if (typeof row.declaration === 'string') row.declaration = TRIMMED.has(row.classification) ? span.trim() : span;
    else if (row.declaration && row.source.path === PACKAGE) row.declaration = JSON.parse(newText(PACKAGE)).exports[row.name];
    return row;
  };

  // Rows: carried forward, with added rows inserted in source order and removed rows marked.
  const rows = [];
  const removedRoot = new Set(before.root.filter(row => !after.root.some(item => item.name === row.name)).map(row => row.name));
  const removedFlags = new Set(before.flags.filter(row => !after.flags.some(item => item.spellings[0] === row.spellings[0])).map(row => row.spellings[0]));
  for (const original of template.rows) {
    const removed = (original.classification.startsWith('public-root-') && removedRoot.has(original.name)) || (original.classification === 'built-in-parser-flag' && removedFlags.has(original.name));
    if (removed) {
      const row = structuredClone(original); row.targetStatus = { removedUpstream: true, at: commit, note: 'Absent at ' + tag + '; source anchors retained from ' + template.sourceSha + '.' };
      changes.removed.push(row.id); rows.push(row); continue;
    }
    rows.push(finishRow(structuredClone(original), original));
  }
  const insertAfter = (predecessorId, row) => { const index = rows.findIndex(item => item.id === predecessorId); assert(index >= 0, 'Missing predecessor ' + predecessorId); rows.splice(index + 1, 0, row); changes.added.push(row.id); };
  const rename = (value, pairs) => typeof value === 'string' ? pairs.reduce((text, [from, to]) => text.replace(new RegExp('(?<![\\w-])' + from.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '(?![\\w-])', 'g'), to), value)
    : Array.isArray(value) ? value.map(item => rename(item, pairs)) : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([key, item]) => [key, rename(item, pairs)])) : value;
  const body = newText(INDEX);
  for (const [index, item] of after.root.entries()) {
    if (before.root.some(row => row.name === item.name)) continue;
    // Sibling: the nearest earlier root export of the same origin and kind.
    const sibling = [...after.root.slice(0, index)].reverse().find(row => row.origin === item.origin && row.typeOnly === item.typeOnly && before.root.some(old => old.name === row.name));
    assert(sibling, 'No sibling row to clone for root export ' + item.name);
    const template_ = template.rows.find(row => row.id === 'sdk.root.' + sibling.name);
    const row = rename(structuredClone(template_), [[sibling.name, item.name]]);
    row.source.location = locate(body, item.start, item.end); row.originalSymbol = item.originalSymbol;
    const predecessor = after.root[index - 1].name;
    insertAfter('sdk.root.' + predecessor, finishRow(row, null));
  }
  const args = newText(ARGS);
  for (const [index, branch] of after.flags.entries()) {
    const name = branch.spellings[0]; if (before.flags.some(row => row.spellings[0] === name)) continue;
    const boolean = /result\.\w+ = true;/.test(args.slice(branch.start, after.flags[index + 1]?.start ?? branch.start + 200));
    const siblingId = boolean ? 'cli.flag.no-session' : 'cli.flag.session-dir', sibling = template.rows.find(row => row.id === siblingId);
    const field = args.slice(branch.start).match(/result\.(\w+) =/)[1], short = name.replace(/^--/, '');
    const row = rename(structuredClone(sibling), [[sibling.name, name], [sibling.name.replace(/^--/, ''), short], [sibling.outputFields[0], field]]);
    row.aliases = branch.spellings.slice(1); row.outputFields = [field];
    row.source.location = locate(args, branch.start, after.flags[index + 1].start);
    insertAfter(rows.find(item => item.classification === 'built-in-parser-flag' && item.name === after.flags[index - 1].spellings[0]).id, finishRow(row, null));
  }

  const inventory = {};
  for (const [key, value] of Object.entries(template)) {
    inventory[key] = structuredClone(value);
    if (key === 'sourceSha' && !same) inventory.sourceTag = tag;
  }
  Object.assign(inventory, { baselineId, sourceSha: commit, sourceTree: same ? template.sourceTree : tree, createdAt: same ? template.createdAt : createdAt });
  if (!same) {
    const position = Object.keys(inventory).indexOf('createdAt') + 1, entries = Object.entries(inventory);
    entries.splice(position, 0, ['previousInventory', { path: templatePath, sha256: templateSha256, baselineId: template.baselineId }], ['refresh', {
      generator: 'tools/SurfaceInventory/build-command-rpc-inventory.mjs',
      method: 'Row text carried from the previous inventory; every source pin, anchor, embedded declaration and count re-derived from canonical Git blob bytes at the target commit. Unchanged files keep their anchors; anchors in changed files are relocated through a line-level LCS mapping, union members and root exports through censuses, and parser branches end where the next branch starts. Censuses detect added and removed surfaces.',
      summary: changes,
    }]);
    for (const key of Object.keys(inventory)) delete inventory[key];
    Object.assign(inventory, Object.fromEntries(entries));
    inventory.provenance = { ...inventory.provenance,
      method: 'Generated by tools/SurfaceInventory/build-command-rpc-inventory.mjs from `git show <pinned commit>:<path>` canonical blob bytes; no source transforms, source execution, build, installed dependency evaluation, paid calls or network calls.',
      cleanCheckoutBefore: cleanCheckout?.before ?? null, cleanCheckoutAfter: cleanCheckout?.after ?? null,
      staticReviewChecks: 'Generator checks: v0.99.1 censuses reproduce every v0.99.1 row name/anchor; recorded locations agree with their offsets; added/removed surfaces detected by census; unique row ids; counts recomputed. Metadata checks, not implementation or behavior tests.' };
  }
  inventory.sourceFiles = template.sourceFiles.map(file => {
    const bytes = readNew(file.path), pin = { path: file.path, scope: file.scope, gitBlobId: gitBlob(bytes), sha256: sha256(bytes), bytes: bytes.length };
    const local = checkout ? checkout(file.path) : null;
    if (local) Object.assign(pin, { checkoutSha256: sha256(local), checkoutBytes: local.length, checkoutMatchesCanonicalGitBlob: local.equals(bytes) });
    else if (pin.sha256 === file.sha256) Object.assign(pin, { checkoutSha256: file.checkoutSha256, checkoutBytes: file.checkoutBytes, checkoutMatchesCanonicalGitBlob: file.checkoutMatchesCanonicalGitBlob });
    else Object.assign(pin, { checkoutSha256: null, checkoutBytes: null, checkoutMatchesCanonicalGitBlob: null });
    return pin;
  });
  inventory.rows = rows;
  inventory.completenessChecks = completeness(template.completenessChecks, rows, inventory.sourceFiles.length);
  if (overlay) {
    applyOverlay(inventory, overlay);
    const rowsValue = inventory.rows; delete inventory.rows; inventory.rows = rowsValue; // rows stay last
  }
  return inventory;
}

function completeness(previous, rows, files) {
  const live = rows.filter(row => !row.targetStatus?.removedUpstream), of = kind => live.filter(row => row.classification === kind);
  const flags = of('built-in-parser-flag'), commands = of('rpc-stdin-command'), events = of('rpc-json-session-event'), root = live.filter(row => row.classification.startsWith('public-root-'));
  const dispatchOrder = commands.map(row => row.dispatchSource?.location.startOffset ?? -1);
  const classificationCounts = {};
  for (const key of Object.keys(previous.classificationCounts)) classificationCounts[key] = 0;
  for (const row of live) classificationCounts[row.classification] = (classificationCounts[row.classification] ?? 0) + 1;
  return {
    coreParserPrimaryFlags: flags.length,
    coreParserSpellingsIncludingAliases: flags.reduce((sum, row) => sum + 1 + row.aliases.length, 0),
    builtInSlashNames: of('built-in-slash-registry-entry').length,
    rpcCommandVariants: commands.length,
    rpcDispatchCases: commands.filter(row => row.dispatchSource).length,
    commandUnionMatchesDispatchOrder: dispatchOrder.every((value, index) => value >= 0 && (index === 0 || value > dispatchOrder[index - 1])),
    successResponseVariants: of('rpc-stdout-success-response').length,
    genericErrorResponseVariants: of('rpc-stdout-error-response').length,
    sdkRootNamedExports: root.length,
    sdkRootRuntimeValues: root.filter(row => !row.typeOnly).length,
    sdkRootTypeOnlyExports: root.filter(row => row.typeOnly).length,
    sdkRootWildcardExports: 0,
    uniqueRowIds: new Set(rows.map(row => row.id)).size === rows.length,
    rowCount: rows.length,
    classificationCounts,
    forwardedSessionEventVariants: events.length,
    forwardedSessionEventDiscriminators: new Set(events.map(row => row.name.split('.')[0])).size,
    nestedAssistantEventDeclarationVariants: of('nested-assistant-event-declaration').length,
    consultedCanonicalFiles: files,
  };
}

// Overlay: { rows: { <row id>: <deep patch> }, top-level keys: <deep patch> }.
export function applyOverlay(inventory, overlay) {
  const merge = (target, patch) => { for (const [key, value] of Object.entries(patch)) { if (value && typeof value === 'object' && !Array.isArray(value) && target[key] && typeof target[key] === 'object' && !Array.isArray(target[key])) merge(target[key], value); else target[key] = structuredClone(value); } return target; };
  for (const [key, value] of Object.entries(overlay)) {
    if (key === '$comment') continue;
    if (key === 'rows') for (const [id, patch] of Object.entries(value)) { const row = inventory.rows.find(item => item.id === id); assert(row, 'Overlay names unknown row ' + id); merge(row, patch); }
    else if (key === 'nativeOnlySurfaces') inventory[key] = structuredClone(value);
    else inventory[key] = inventory[key] && typeof inventory[key] === 'object' && !Array.isArray(inventory[key]) ? merge(inventory[key], value) : structuredClone(value);
  }
  return inventory;
}

// Top level indented two spaces; one row object per physical line.
export function serialize(inventory) {
  const marker = '"__ROWS__"', text = JSON.stringify({ ...inventory, rows: '__ROWS__' }, null, 2);
  assert.equal(text.split(marker).length, 2);
  return text.replace(marker, '[\n' + inventory.rows.map(row => '    ' + JSON.stringify(row)).join(',\n') + '\n  ]') + '\n';
}

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 2) { assert(argv[i].startsWith('--'), 'Unexpected argument ' + argv[i]); args[argv[i].slice(2)] = argv[i + 1]; }
  for (const name of ['upstream', 'lock', 'template']) assert(args[name], '--' + name + ' is required');
  assert(!!args.out !== !!args.check, 'Pass exactly one of --out or --check');
  return args;
}

export function main(argv = process.argv.slice(2)) {
  const args = parseArgs(argv), upstream = resolve(args.upstream);
  const lock = JSON.parse(readFileSync(resolve(repo, args.lock), 'utf8'));
  const templateBytes = readFileSync(resolve(repo, args.template)), template = JSON.parse(templateBytes.toString('utf8'));
  const git = (...command) => execFileSync('git', ['-C', upstream, ...command]).toString().trim();
  const clean = () => git('status', '--porcelain', '--untracked-files=all') === '';
  const cleanBefore = clean(), head = git('rev-parse', 'HEAD');
  const readNew = gitReader(upstream, lock.source.commit), readOld = gitReader(upstream, template.sourceSha);
  for (const file of template.sourceFiles) {
    const pin = lock.artifacts.find(row => row.kind === 'source-file' && row.path === file.path);
    if (pin) assert.equal(sha256(readNew(file.path)), pin.sha256, file.path + ' differs from ' + args.lock);
  }
  const checkout = head === lock.source.commit && cleanBefore ? path => readFileSync(resolve(upstream, path)) : null;
  const overlay = args.overlay ? JSON.parse(readFileSync(resolve(repo, args.overlay), 'utf8')) : null;
  const inventory = buildInventory({ template, readOld, readNew, commit: lock.source.commit, tree: lock.source.tree, tag: lock.source.tag, createdAt: lock.source.acquiredAt, checkout, overlay,
    templatePath: args.template.replaceAll('\\', '/'), templateSha256: sha256(templateBytes), cleanCheckout: { before: cleanBefore, after: clean() } });
  const bytes = Buffer.from(serialize(inventory));
  if (args.out) { writeFileSync(resolve(repo, args.out), bytes); process.stdout.write(JSON.stringify({ wrote: args.out, sha256: sha256(bytes), rows: inventory.rows.length, refresh: inventory.refresh?.summary ?? null }, null, 2) + '\n'); return; }
  const target = resolve(repo, args.check); assert(existsSync(target), args.check + ' missing');
  if (!readFileSync(target).equals(bytes)) { process.stderr.write(args.check + ' does not match the generated inventory\n'); process.exitCode = 1; return; }
  process.stdout.write(JSON.stringify({ reproduced: args.check, sha256: sha256(bytes), rows: inventory.rows.length }, null, 2) + '\n');
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
