// Inert pinned-source inventory validation. Never imports upstream modules,
// invokes Git/compiler/process/network, extracts archives, or writes a file.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, lstatSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve, sep, posix } from 'node:path';
import { fileURLToPath } from 'node:url';

const SOURCE_SHA = 'd86654abb8862e201933517d6f1fce9f88dd117f';
const EXT = 'packages/coding-agent/src/core/extensions/';
const TYPES = EXT + 'types.ts', RUNNER = EXT + 'runner.ts';
const own = fileURLToPath(import.meta.url), repo = resolve(dirname(own), '../..');
const inventoryPath = 'compatibility/extensions/native-surface.json';
const inventorySha256 = '11cc23450f834eab5c4d6dfe31b2e8e518be1bb64cdd69b6a9aa1f168b026b4f';
const hash = (value, algorithm = 'sha256') => createHash(algorithm).update(value).digest('hex');
const blob = bytes => hash(Buffer.concat([Buffer.from('blob ' + bytes.length + '\0'), bytes]), 'sha1');
const lineAt = (text, offset) => text.slice(0, offset).split('\n').length;
const unique = values => [...new Set(values)];

// This is a bounded lexical census, NOT a TypeScript AST/type checker. It keeps
// exact signatures (including nested inline object/callback types). Unknown
// declaration/member grammar in the pinned census inputs fails explicitly.
export function tokens(text) {
  const result = []; let i = 0;
  while (i < text.length) {
    const start = i, c = text[i];
    if (/\s/.test(c)) { i++; continue; }
    if (text.startsWith('//', i)) { i = text.indexOf('\n', i); if (i < 0) break; continue; }
    if (text.startsWith('/*', i)) { const end = text.indexOf('*/', i + 2); assert(end >= 0, 'Unclosed comment'); i = end + 2; continue; }
    if (c === '"' || c === "'" || c === '`') {
      i++; while (i < text.length && text[i] !== c) { if (text[i] === '\\') i++; i++; }
      assert(i < text.length, 'Unclosed string/template'); i++;
      result.push({ value: text.slice(start, i), kind: 'string', start, end: i }); continue;
    }
    if (/[A-Za-z_$]/.test(c)) { i++; while (i < text.length && /[A-Za-z0-9_$]/.test(text[i])) i++; }
    else i++;
    result.push({ value: text.slice(start, i), kind: 'token', start, end: i });
  }
  const stack = [], opening = { '(': ')', '[': ']', '{': '}' };
  for (let n = 0; n < result.length; n++) {
    const token = result[n]; if (token.kind === 'string') continue;
    if (opening[token.value]) stack.push(n);
    else if ([')', ']', '}'].includes(token.value)) {
      const start = stack.pop(); assert(start !== undefined && opening[result[start].value] === token.value, 'Mismatched delimiter');
      result[start].pair = n; token.pair = start;
    }
  }
  assert.equal(stack.length, 0, 'Unclosed delimiter'); return result;
}

function sourceRef(path, text, start, end) {
  const exact = text.slice(start, end);
  return { path, startLine: lineAt(text, start), endLine: lineAt(text, end - 1), startUtf16: start, endUtf16: end, sha256: hash(Buffer.from(exact)) };
}
function callbackKind(signature, memberKind) {
  const callable = memberKind === 'method' || signature.includes('=>') || /Handler|Renderer|Transformer|Factory|ProviderImages|ProviderClassifier/.test(signature);
  if (!callable) return 'data-or-imported-capability';
  if (memberKind === 'method') {
    const ts = tokens(signature), opening = ts.findIndex(row => row.value === '('); assert(opening >= 0);
    const returned = signature.slice(ts[ts[opening].pair].end).replace(/^\s*:\s*/, '');
    if (returned.startsWith('Promise<')) return 'promise-returning-as-declared';
    if (!returned.includes('Promise<')) return 'synchronous-return; nested callbacks retained-as-declared';
  }
  if (signature.includes('Promise<')) return 'contains-promise-callback-or-return; exact-alternatives-retained-as-declared';
  return 'synchronous-or-delegated-callback-as-declared';
}
export function census(text, path) {
  const ts = tokens(text), declarations = [], exports = [], imports = [];
  let i = 0;
  const exact = (start, end) => text.slice(ts[start].start, ts[end].end);
  const semi = start => { for (let p = start; p < ts.length; p++) { if (ts[p].value === ';') return p; if (ts[p].pair > p) p = ts[p].pair; } throw new Error('Missing declaration semicolon'); };
  while (i < ts.length) {
    const start = i; let exported = false;
    if (ts[i].value === 'import') { const end = semi(i); const signature = exact(i, end); imports.push({ signature, source: sourceRef(path, text, ts[i].start, ts[end].end) }); i = end + 1; continue; }
    if (ts[i].value === 'export') {
      exported = true; i++;
      if (ts[i].value === 'type' && ts[i + 1]?.value === '{') i++;
      if (ts[i].value === '{') {
        const end = semi(i), signature = exact(start, end), group = ts.slice(i + 1, ts[i].pair);
        const from = signature.match(/\bfrom\s+["']([^"']+)["']/)?.[1] ?? null;
        const names = []; for (let p = 0; p < group.length; p++) if (/^[A-Za-z_$]/.test(group[p].value) && group[p].value !== 'type') {
          const original = group[p].value; let name = original;
          if (group[p + 1]?.value === 'as') { name = group[p + 2].value; p += 2; }
          names.push({ name, original });
        }
        exports.push({ kind: ts[start + 1].value === 'type' ? 'type' : 'mixed-or-value', from, names, signature, source: sourceRef(path, text, ts[start].start, ts[end].end) });
        i = end + 1; continue;
      }
    }
    if (ts[i]?.value === 'async') i++;
    const kind = ts[i]?.value;
    if (!['interface', 'type', 'function'].includes(kind)) { if (ts[i]?.pair > i) i = ts[i].pair + 1; else i++; continue; }
    const name = ts[i + 1]?.value; assert(/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(name)); let end, body;
    if (kind === 'type') end = semi(i);
    else {
      for (let p = i + 2; p < ts.length; p++) {
        if (ts[p].value === ';') { end = p; break; }
        if (ts[p].value === '{') { body = p; end = ts[p].pair; if (ts[end + 1]?.value === ';') end++; break; }
        if (ts[p].pair > p) p = ts[p].pair;
      }
    }
    assert(end !== undefined, 'Unsupported declaration end: ' + name);
    const signature = exact(start, end), members = [];
    if (kind === 'interface') {
      assert(body !== undefined); let cursor = body + 1;
      while (cursor < ts[body].pair) {
        const memberStart = cursor; let memberEnd;
        for (let p = cursor; p < ts[body].pair; p++) { if (ts[p].value === ';') { memberEnd = p; break; } if (ts[p].pair > p) p = ts[p].pair; }
        assert(memberEnd !== undefined, 'Unsupported interface member: ' + name);
        let n = memberStart; if (ts[n].value === 'readonly') n++;
        assert(/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(ts[n].value), 'Unsupported member name');
        const memberName = ts[n].value, after = ts[n + 1]?.value, methodAfter = after === '?' ? ts[n + 2]?.value : after, memberKind = methodAfter === '(' || methodAfter === '<' ? 'method' : 'property';
        const memberSignature = exact(memberStart, memberEnd), ordinal = members.filter(m => m.name === memberName).length + 1;
        members.push({ id: name + '.' + memberName + '#' + ordinal, name: memberName, overload: ordinal, kind: memberKind, optional: after === '?', readonly: ts[memberStart].value === 'readonly', callbackShape: callbackKind(memberSignature, memberKind), signature: memberSignature, source: sourceRef(path, text, ts[memberStart].start, ts[memberEnd].end) });
        cursor = memberEnd + 1;
      }
    }
    const header = body === undefined ? signature : text.slice(ts[start].start, ts[body].start);
    const inherited = header.match(/\bextends\s+([\s\S]*)/)?.[1].trim() ?? null;
    const overload = declarations.filter(row => row.name === name).length + 1;
    declarations.push({ id: name + '#' + overload, name, overload, kind, exported, inherited, source: sourceRef(path, text, ts[start].start, ts[end].end), signatureSha256: hash(Buffer.from(signature)), members });
    i = end + 1;
  }
  return { path, declarations, exports, imports };
}

const REDUCERS = {
  observation: { method: 'emit', family: 'observation', result: 'ignored', behavior: 'Captured handler-array snapshot; extension order then registration order; await each handler; report thrown handler error and continue.', error: 'reported-and-continue', mutation: 'The same event object is supplied; this source does not enforce deep read-only observation DTOs.' },
  sessionPre: { method: 'emit', family: 'session-pre-action', result: 'event-specific SessionBefore*Result', behavior: 'Every truthy result replaces the stored result; first result.cancel short-circuits. This is not a field-patch merge.', error: 'reported-and-continue; a throw is not a cancel decision', mutation: 'Shared event object; returned optional fields are event-specific.' },
  trust: { method: 'emitProjectTrustEvent', family: 'project-trust', result: 'ProjectTrustEventResult', behavior: 'Await ordered snapshot handlers; trusted undecided continues; first other returned decision ends dispatch. Missing/invalid values are not generally validated by this helper.', error: 'collected-and-continue', mutation: 'Restricted ProjectTrustContext, before project code activation; factory/resource trust integration remains separate.' },
  resources: { method: 'emitResourcesDiscover', family: 'resource-discovery', result: 'ResourcesDiscoverResult', behavior: 'Aggregate skill/prompt/theme paths in handler order and attach extensionPath provenance.', error: 'reported-and-continue', mutation: 'Fresh event for each handler; contributions are aggregated, not whole-result replacement.' },
  input: { method: 'emitInput', family: 'input-transform', result: 'InputEventResult', behavior: 'transform chains text; images uses nullish fallback; handled stops; final no-change continue uses reference/value comparison.', error: 'reported-and-continue', mutation: 'Fresh event contains current text/images; output action discriminant is consumed, not a generic reducer.' },
  toolCall: { method: 'emitToolCall', family: 'tool-call', result: 'ToolCallEventResult', behavior: 'Shared event.input can be mutated in place; latest truthy result stored; first block stops; no proposed-arguments replacement field exists in baseline result.', error: 'propagates; execution must be blocked by caller pipeline', mutation: 'In-place arguments must translate to native immutable replacements before final validation/authorization.' },
  toolResult: { method: 'emitToolResult', family: 'tool-result', result: 'ToolResultEventResult', behavior: 'Compose content/details/structuredContent/isError/usage fields only when not undefined; content without non-undefined structuredContent deletes stale structuredContent; return undefined if no tracked patch.', error: 'reported-and-continue', mutation: 'Shallow copy of event retains referenced input/content/details; null differs from undefined; source result has no terminate patch field.' },
  messageEnd: { method: 'emitMessageEnd', family: 'final-message', result: 'MessageEndEventResult', behavior: 'Truthy message replacements compose; replacement role must match current role or is diagnosed and skipped.', error: 'reported-and-continue', mutation: 'Fresh event points to current finalized message; in-place mutation is not independently flagged as returned modification.' },
  context: { method: 'emitContext', family: 'conversation-context', result: 'ContextEventResult', behavior: 'Clone starting messages; run conversation-only context first; returned or changed list restores replayed system/tool state. Then run full context_with_system transforms.', error: 'reported-and-continue', mutation: 'Conversation array reference/order detection is shallow; original system/tool state is restored on changed list.' },
  contextSystem: { method: 'emitContext', family: 'full-transcript-context', result: 'ContextEventResult', behavior: 'After all context handlers, full messages are supplied; returned list uses nullish fallback; loss of leading system is reported but output honored.', error: 'reported-and-continue; diagnostic does not restore dropped leading system', mutation: 'Full array and rich message objects are mutable; handler owns returned system/tool declarations.' },
  request: { method: 'emitBeforeProviderRequest', family: 'provider-request', result: 'BeforeProviderRequestEventResult (unknown)', behavior: 'Every non-undefined return replaces the current payload, including null; next handler sees replacement.', error: 'reported-and-continue', mutation: 'Payload remains arbitrary JS unknown; cyclic/functions/opaque objects need explicit native/bridge admission.' },
  headers: { method: 'emitBeforeProviderHeaders', family: 'provider-headers', result: 'ignored', behavior: 'Await ordered mutations to shared headers; return values ignored. ProviderHeaders null deletes header at provider boundary.', error: 'reported-and-continue', mutation: 'Mutation-derived patches; missing/null/undefined/casing must be qualified separately at HTTP boundary.' },
  prompt: { method: 'emitBeforeAgentStart', family: 'prompt-before-agent', result: 'BeforeAgentStartEventResult', behavior: 'Normalize shared prompt sections; mutations visible to later handlers; aggregate truthy message contributions; explicit systemPrompt sets forceSystemPrompt.', error: 'reported-and-continue', mutation: 'Rich mutable NormalizedBuildSystemPromptOptions plus getter-rendered current prompt; native sections/replacements must preserve sequencing.' },
  boundary: { method: 'emitBoundary', family: 'turn-or-settle-boundary', result: 'BoundaryResult', behavior: 'Compose entries/continue when not undefined and also observe shared-array mutations; rebuild/validate preview after each handler. Final invalid state returns entries empty, continue false, valid false; a later handler may repair prior invalid entries.', error: 'handler errors reported; invalid preview diagnosed; source dispatcher itself does not commit entries', mutation: 'Host preview contains branch entries/context/LLM/pending messages; actual append and continuation policy belongs to session runtime.' },
  cache: { method: 'emitCacheWarmingDecision', family: 'cache-warming', result: 'CacheWarmingDecisionEventResult', behavior: 'Start with event.action; last explicit non-undefined action wins.', error: 'reported-and-continue', mutation: 'Shared event input; action validity/runtime scheduling is separate from reducer.' },
  shell: { method: 'emitUserBash', family: 'user-shell', result: 'UserBashEventResult', behavior: 'Undefined falls through; first valid exactly-one operations/result claims. Invalid result or throw is diagnosed and rethrown; no local-shell fallback.', error: 'reported-and-rethrow', mutation: 'operations.exec is executable callback; BashResult exitCode own-field admission differs from ordinary JSON optional omission.' },
};
const EVENT_RULES = {
  project_trust: 'trust', resources_discover: 'resources',
  session_before_switch: 'sessionPre', session_before_fork: 'sessionPre', session_before_compact: 'sessionPre', session_before_tree: 'sessionPre',
  context: 'context', context_with_system: 'contextSystem', cache_warming_decision: 'cache',
  before_provider_request: 'request', before_provider_headers: 'headers', before_agent_start: 'prompt',
  agent_before_settle: 'boundary', turn_end: 'boundary', message_end: 'messageEnd', tool_call: 'toolCall', tool_result: 'toolResult', user_bash: 'shell', input: 'input',
};
const OWNERS = {
  registry: { owner: 'PiSharp.Extensions.Runtime registration coordinator', contractOwner: 'PiSharp.Extensions.Abstractions', phase: ['P6-01', 'P6-03'], dependencies: ['P3 awaited tool/event barriers', 'P4 session generations', 'P6 trust/lifetime'] },
  action: { owner: 'PiSharp.Extensions.Runtime host action broker', contractOwner: 'PiSharp.Extensions.Abstractions host-owned DTOs', phase: ['P6-06', 'P6-07'], dependencies: ['P3 final argument policy/nested IDs', 'P4 durable branch transactions', 'P5 mode capabilities'] },
  context: { owner: 'PiSharp.Extensions.Runtime generation-checked context host', contractOwner: 'PiSharp.Extensions.Abstractions snapshot/capability interfaces', phase: ['P6-01', 'P6-06', 'P6-07'], dependencies: ['P2 model/auth broker', 'P3 operation cancellation', 'P4 branch generation', 'P5 UI capability'] },
  ui: { owner: 'PiSharp UI adapter host with extension-owned handles', contractOwner: 'PiSharp.Extensions.Abstractions for dialogs; optional PiSharp.Tui.Abstractions for components', phase: ['P6-08'], dependencies: ['P5 TUI/RPC/headless implementations', 'P6 owner disposal'] },
  tool: { owner: 'PiSharp.Extensions.Runtime tool adapter through P3 pipeline', contractOwner: 'PiSharp.Extensions.Abstractions host-owned descriptor/schema/progress/result DTOs', phase: ['P6-06'], dependencies: ['P2 normalized content/usage', 'P3 validation/authorization/execution IDs/mutation queue', 'P4 declared tool transcript'] },
  provider: { owner: 'PiSharp provider/catalog/auth adapters with extension ownership', contractOwner: 'PiSharp.Extensions.Abstractions host-owned provider capabilities; existing P2 contracts', phase: ['P6-08'], dependencies: ['P2 catalog/transports/auth', 'P3 awaited instrumentation', 'P4 restoration', 'P6 lifetime'] },
  events: { owner: 'PiSharp.Extensions.Runtime typed dispatch and event-specific reducers', contractOwner: 'PiSharp.Extensions.Abstractions host-owned event/result DTOs', phase: ['P6-02'], dependencies: ['P3 barriers', 'P4 boundary/branch/session runtime', 'P5 UI prompt capability', 'P2 provider instrumentation'] },
  internal: { owner: 'PiSharp.Extensions.Runtime host implementation; no direct stable service/container exposure', contractOwner: 'Host implementation boundary pending API approval', phase: ['P6-01', 'P6-03', 'P6-09'], dependencies: ['P6 staged registration/snapshot/lifetime/reload', 'P4 generation invalidation'] },
};
function category(name) {
  if (name === 'ExtensionAPI') return 'registry';
  if (/^(ExtensionUI|ExtensionWidget|WorkingIndicator|WidgetPlacement|TerminalInput|EditorFactory|AutocompleteProviderFactory|MessageRender|EntryRender|MarkdownTransform)/.test(name)) return 'ui';
  if (/^(ExtensionContext$|ExtensionToolContext$|ExtensionCommandContext$|ReplacedSessionContext$|ProjectTrustContext$|ContextUsage$|CompactOptions$|ExecuteToolOptions$)/.test(name)) return 'context';
  if (/^(ToolDefinition|AnyToolDefinition|ToolAnnotations|ToolNamespace|ToolLoadout|ToolExposure|ToolRender|ToolInfo|defineTool)/.test(name)) return 'tool';
  if (/^(Provider.*Config|ExtensionVirtualModel)/.test(name)) return 'provider';
  if (/Event|Boundary|EntryDraft|^InputSource$|^ModelSelectSource$|^UIPromptKind$|^AgentActivityOutcome$|^TreePreparation$|^ProjectTrustHandler$/.test(name)) return 'events';
  if (/Handler$|ExtensionActions$/.test(name)) return 'action';
  return 'internal';
}
function interfacePermission(name) {
  if (name === 'ProjectTrustContext') return 'trust-only UI subset; no active ordinary/command/session context';
  if (name === 'ExtensionToolContext') return 'ordinary context + callable tools + nested P3 pipeline; no command-only wait/replacement';
  if (name === 'ExtensionCommandContext') return 'ordinary context + command-only idle/reload/new/fork/tree/switch; setup callback exposes full source SessionManager';
  if (name === 'ReplacedSessionContext') return 'fresh replacement-session command context + awaited sendMessage/sendUserMessage';
  if (name === 'ExtensionContext') return 'ordinary event/tool context; read-only session facade is typed only; source concrete ModelRegistry exposed; compact fire-and-forget, abort/shutdown bounded host actions';
  if (name === 'ExtensionUIContext') return 'mode-dependent UI; source no-UI implementation returns false/undefined/empty/no-op; native capability and safe diagnostics required';
  if (name === 'ExtensionAPI') return 'factory registration allowed; host action stubs throw until bound; exec directly launches OS command in source; trusted plugins are not sandboxed';
  return 'host-owned DTO/callback projection or internal adapter boundary; stable native capability assignment pending';
}
function apiOwner(member) {
  if (/Provider|Mcp|VirtualModel/.test(member)) return OWNERS.provider;
  if (/Renderer|MarkdownTransformer/.test(member)) return OWNERS.ui;
  if (member === 'registerTool') return OWNERS.tool;
  if (/^(send|append|set)|^exec$|^getSessionName$/.test(member)) return OWNERS.action;
  return OWNERS.registry;
}
function methodRef(path, text, name) {
  const match = new RegExp('(^|\\n)(?:export (?:async )?function |\\t(?:private )?(?:async )?)' + name + '(?:<[^\\n]*>)?\\(').exec(text);
  assert(match, 'Missing source method ' + name); const start = match.index + (match[1] ? 1 : 0);
  const lines = text.slice(start).split('\n'); let count = lines.length;
  // Method anchors stop at the next peer method/declaration; whole-file hash is
  // still authoritative, and the exact recorded range is validated byte-for-byte.
  for (let i = 1; i < lines.length; i++) if (/^(?:export (?:async )?function |\t(?:private )?(?:async )?)[A-Za-z_$][\w$]*(?:<[^\n]*>)?\(/.test(lines[i])) { count = i; break; }
  const end = start + lines.slice(0, count).join('\n').length;
  return { declaration: name, ...sourceRef(path, text, start, end) };
}
function sourcePin(path, bytes) { return { path, bytes: bytes.length, sha256: hash(bytes), gitBlob: blob(bytes), sourceUrl: 'https://github.com/earendil-works/pi/blob/' + SOURCE_SHA + '/' + path }; }
function importsIn(text, path) {
  const result = [];
  for (const match of text.matchAll(/^import(?:\s|\{)[\s\S]*?;/gm)) {
    const from = match[0].match(/\bfrom\s+["']([^"']+)["']/)?.[1]; if (!from) continue;
    result.push({ specifier: from, form: /^import\s+type\b/.test(match[0]) ? 'type-only' : 'runtime-or-mixed', signature: match[0], source: sourceRef(path, text, match.index, match.index + match[0].length) });
  }
  for (const match of text.matchAll(/\bimport\(["']([^"']+)["']\)/g)) result.push({ specifier: match[1], form: 'dynamic-or-import-type-text', signature: match[0], source: sourceRef(path, text, match.index, match.index + match[0].length) });
  return result;
}
function classMethods(text, path, className, selectedNames = null) {
  const result = [], classStart = text.indexOf('export class ' + className + ' {'); assert(classStart >= 0);
  const remaining = text.slice(classStart);
  for (const match of remaining.matchAll(/^\t(?:async )?([A-Za-z_$][\w$]*)(?:<[^\n]*>)?\(/gm)) {
    const name = match[1]; if (name === 'constructor' || (selectedNames && !selectedNames.includes(name))) continue;
    const start = classStart + match.index; let cursor = start, parentheses = 0, brackets = 0, quoted = null;
    for (; cursor < text.length; cursor++) {
      const c = text[cursor]; if (quoted) { if (c === '\\') cursor++; else if (c === quoted) quoted = null; continue; }
      if (c === '"' || c === "'") { quoted = c; continue; }
      if (c === '(') parentheses++; else if (c === ')') parentheses--; else if (c === '[') brackets++; else if (c === ']') brackets--;
      else if (!parentheses && !brackets && (c === '{' || c === ';')) break;
    }
    assert(cursor < text.length); const end = text[cursor] === ';' ? cursor + 1 : cursor;
    const signature = text.slice(start, end).trim(), overload = result.filter(row => row.name === name).length + 1;
    result.push({ id: className + '.' + name + '#' + overload, name, overload, signature, source: sourceRef(path, text, start, end), callbackShape: callbackKind(signature, 'method'), required: true, nativeBehavior: 'Deferred', owner: className === 'ModelRegistry' ? OWNERS.provider : OWNERS.context, nativeAbi: 'Host-owned capability interface required; concrete source class is excluded from stable core ABI', fixtureIds: [] });
  }
  if (selectedNames) assert.deepEqual(unique(result.map(row => row.name)).sort(), [...selectedNames].sort());
  return result;
}
function pickNames(text, name) {
  const match = new RegExp('export type ' + name + '\\s*=\\s*Pick<[\\s\\S]*?>;').exec(text); assert(match);
  return { names: [...match[0].matchAll(/"([^"]+)"/g)].map(row => row[1]), signature: match[0], start: match.index, end: match.index + match[0].length };
}
function guideRef(text, path, heading) { const start = text.indexOf(heading); assert(start >= 0); const next = text.indexOf('\n##', start + heading.length); return { heading, ...sourceRef(path, text, start, next < 0 ? text.length : next) }; }

// Authoring helper accepts inert byte maps. It does not read/write files or run
// any module; main below is the independent fixed archive-byte validation path.
export function buildSurface(source, phase6, phase7) {
  const text = path => { const bytes = source.get(path); assert(bytes, 'Missing retained source member: ' + path); return bytes.toString('utf8'); };
  const types = census(text(TYPES), TYPES), busPath = 'packages/coding-agent/src/core/event-bus.ts', bus = census(text(busPath), busPath);
  const indexPath = EXT + 'index.ts', packageIndex = 'packages/coding-agent/src/index.ts';
  const barrel = census(text(indexPath), indexPath), rootExports = census(text(packageIndex), packageIndex).exports.filter(row => row.from === './core/extensions/index.ts').flatMap(row => row.names.map(row => row.name));
  const extensionExports = unique(barrel.exports.flatMap(row => row.names.map(row => row.name)));
  const primary = [TYPES, indexPath, RUNNER, EXT + 'loader.ts', EXT + 'wrapper.ts', EXT + 'virtual-modules.ts', EXT + 'jiti-loader.ts', EXT + 'jiti-static-loader.ts', busPath];
  const dependencies = primary.flatMap(path => importsIn(text(path), path));
  const sourcePaths = new Set([...primary, packageIndex, 'package-lock.json', 'packages/coding-agent/package.json', 'packages/agent/package.json', 'packages/ai/package.json', 'packages/tui/package.json', 'packages/coding-agent/docs/extensions.md', 'packages/coding-agent/docs/rpc-extension-ui.md', 'packages/coding-agent/docs/custom-provider.md', 'packages/coding-agent/docs/virtual-models.md']);
  for (const row of dependencies) if (row.specifier.startsWith('.')) { row.resolvedSourcePath = posix.normalize(posix.join(posix.dirname(row.source.path), row.specifier)); assert(source.has(row.resolvedSourcePath)); sourcePaths.add(row.resolvedSourcePath); }
  const declarations = types.declarations.map(row => {
    const family = category(row.name), ownership = OWNERS[family];
    return { ...row, family, publicExposure: row.exported ? { directTypesModule: true, extensionBarrel: extensionExports.includes(row.name), packageRoot: rootExports.includes(row.name) } : { directTypesModule: false, extensionBarrel: false, packageRoot: false }, required: true, requirement: row.exported ? 'Public declaration or host adapter contract retained' : 'Local supporting declaration retained; reachability/type expansion not claimed', ownership, contextPermission: interfacePermission(row.name), nativeSyntax: { classification: 'IntentionalDifference', approval: 'Pending', rule: 'C# host-owned interfaces/DTOs/delegates replace TypeScript syntax and JS runtime objects; exact ABI names are not frozen by this inventory.' }, behavior: { status: 'Deferred', qualification: 'source-inventory-only', fixtureIds: [] } };
  });
  const api = declarations.find(row => row.name === 'ExtensionAPI'), on = api.members.filter(row => row.name === 'on');
  const events = on.map(row => {
    const name = row.signature.match(/event:\s*"([^"]+)"/)?.[1]; assert(name);
    const handler = row.signature.match(/handler:\s*([\s\S]*?)\s*\)\s*:\s*\(\)\s*=>\s*void;/)?.[1]?.replace(/,\s*$/, ''); assert(handler);
    const eventType = name === 'project_trust' ? 'ProjectTrustEvent' : handler.match(/^ExtensionHandler<([A-Za-z]+Event)/)?.[1]; assert(eventType);
    let eventSource = declarations.find(row => row.name === eventType)?.source;
    if (!eventSource) {
      assert.equal(eventType, 'CacheWarmingDecisionEvent'); const path = 'packages/coding-agent/src/core/cache-warmer.ts', body = text(path), start = body.indexOf('export interface ' + eventType), end = body.indexOf('\n}', start) + 2;
      assert(start >= 0 && end > start); eventSource = sourceRef(path, body, start, end);
    }
    const ruleId = EVENT_RULES[name] ?? 'observation', reducer = REDUCERS[ruleId];
    return { id: 'event:' + name, name, required: true, subscriptionMemberId: row.id, handlerDeclaration: handler, eventType, eventSource, handlerShape: name === 'project_trust' ? 'sync-or-Promise decision; special restricted context' : 'sync-or-Promise result-or-void through ExtensionHandler', context: name === 'project_trust' ? 'ProjectTrustContext' : 'ExtensionContext', allowedModes: ['tui', 'rpc', 'json', 'print'], modeQualification: 'source declaration permits all modes; actual emission and UI availability are host-dependent and unqualified', owner: OWNERS.events, reducerId: ruleId, reducer, dispatchSource: methodRef(RUNNER, text(RUNNER), reducer.method), ordering: 'snapshot per dispatcher, extension order then handler registration order; sequential await inside dispatcher', specialScheduling: name === 'mcp_servers_change' ? 'bindCore installs MCP change listener using void this.emit(...), then reports missing MCP handler; registry change caller does not await dispatcher completion' : name.startsWith('ui_prompt_') ? 'withUIPrompt tracks outermost prompt; emitUIPromptEvent queues a microtask and does not await emit at UI call boundary' : name === 'provider_stream_event' ? 'adapter-owned parsed data is documented read-only; source/wrapper invocation timing requires provider fixture qualification' : name === 'agent_settled' ? 'settled observation; no additional automatic continuation under native P6 contract; source event alone does not enforce full caller behavior' : 'caller integration timing remains Deferred', persistence: 'event itself not implicitly persistent; host session/boundary actions own durable commits', cancellation: 'Promise rejection/AbortSignal is event-specific; no blanket fail-closed or timeout policy', validation: 'exact source reducer consumed fields; no broad schema validation inferred', nativeBehavior: 'Deferred', fixtureIds: [] };
  });
  const familyMembers = ['ExtensionAPI', 'ExtensionContext', 'ExtensionToolContext', 'ExtensionCommandContext', 'ReplacedSessionContext', 'ProjectTrustContext', 'ExtensionUIContext', 'ToolDefinition', 'ToolLoadout', 'RegisteredCommand', 'ProviderConfig', 'ExtensionVirtualModel'];
  const surfaces = familyMembers.flatMap(name => declarations.filter(row => row.name === name).flatMap(declaration => declaration.members.map(member => ({ id: member.id, declaration: name, member: member.name, overload: member.overload, required: true, source: member.source, signatureSha256: hash(Buffer.from(member.signature)), callbackShape: member.callbackShape, owner: name === 'ExtensionAPI' ? apiOwner(member.name) : declaration.ownership, registrationLifetimeOwner: name === 'ExtensionAPI' && (/^register/.test(member.name) || ['on', 'events'].includes(member.name)) ? OWNERS.registry.owner : null, contextPermission: declaration.contextPermission, nativeSyntax: 'IntentionalDifference; Pending approval', nativeBehavior: 'Deferred', fixtureIds: [] }))));
  const busMembers = bus.declarations.filter(row => row.name === 'EventBus').flatMap(row => row.members).map(member => ({ ...member, id: 'ExtensionAPI.events.' + member.name, required: true, nativeBehavior: 'Deferred', owner: OWNERS.registry, behavior: member.name === 'emit' ? 'Synchronous EventEmitter emit returns void; safe async listeners are launched without an awaited publication barrier.' : 'Returns unsubscribe; async safe wrapper awaits handler and catches/logs failure, emitter itself does not await it.' }));
  const sessionPath = 'packages/coding-agent/src/core/session-manager.ts', footerPath = 'packages/coding-agent/src/core/footer-data-provider.ts';
  const sessionPick = pickNames(text(sessionPath), 'ReadonlySessionManager'), footerPick = pickNames(text(footerPath), 'ReadonlyFooterDataProvider');
  const contextFacades = [
    { name: 'ReadonlySessionManager', source: sourceRef(sessionPath, text(sessionPath), sessionPick.start, sessionPick.end), signature: sessionPick.signature, permission: 'Typed Pick of source SessionManager read methods; runtime object is not a security boundary or deep immutable snapshot', members: classMethods(text(sessionPath), sessionPath, 'SessionManager', sessionPick.names) },
    { name: 'ReadonlyFooterDataProvider', source: sourceRef(footerPath, text(footerPath), footerPick.start, footerPick.end), signature: footerPick.signature, permission: 'TUI footer factory read capability and owner-bound branch subscription', members: classMethods(text(footerPath), footerPath, 'FooterDataProvider', footerPick.names) },
    { name: 'ModelRegistry', source: sourcePin('packages/coding-agent/src/core/model-registry.ts', source.get('packages/coding-agent/src/core/model-registry.ts')), permission: 'Source concrete synchronous compatibility facade exposes catalog reads, credentials, provider/model writes and actual model calls; native must split host-owned broker capabilities and permissions', members: classMethods(text('packages/coding-agent/src/core/model-registry.ts'), 'packages/coding-agent/src/core/model-registry.ts', 'ModelRegistry') },
  ];
  const documentation = [
    guideRef(text('packages/coding-agent/docs/extensions.md'), 'packages/coding-agent/docs/extensions.md', '## Respect the runtime lifecycle'),
    guideRef(text('packages/coding-agent/docs/extensions.md'), 'packages/coding-agent/docs/extensions.md', '### Events and concurrency'),
    guideRef(text('packages/coding-agent/docs/extensions.md'), 'packages/coding-agent/docs/extensions.md', '### Tools'),
    guideRef(text('packages/coding-agent/docs/extensions.md'), 'packages/coding-agent/docs/extensions.md', '### Tool exposure'),
    guideRef(text('packages/coding-agent/docs/extensions.md'), 'packages/coding-agent/docs/extensions.md', '### Context and session changes'),
    guideRef(text('packages/coding-agent/docs/extensions.md'), 'packages/coding-agent/docs/extensions.md', '### UI and modes'),
    guideRef(text('packages/coding-agent/docs/rpc-extension-ui.md'), 'packages/coding-agent/docs/rpc-extension-ui.md', '## Limitations'),
    guideRef(text('packages/coding-agent/docs/custom-provider.md'), 'packages/coding-agent/docs/custom-provider.md', '## Register a provider'),
    guideRef(text('packages/coding-agent/docs/custom-provider.md'), 'packages/coding-agent/docs/custom-provider.md', '## Implement custom streaming'),
    guideRef(text('packages/coding-agent/docs/virtual-models.md'), 'packages/coding-agent/docs/virtual-models.md', '## Keep routing state'),
  ];
  const lock = JSON.parse(text('package-lock.json'));
  const externalPackages = unique(dependencies.map(row => row.specifier).filter(value => !value.startsWith('.') && !value.startsWith('node:')).map(value => value.startsWith('@') ? value.split('/').slice(0, 2).join('/') : value.split('/')[0])).map(name => ({ name, workspace: name.startsWith('@earendil-works/'), lock: lock.packages['node_modules/' + name] ?? null, stableAbi: 'No JS package/runtime object dependency permitted in the core native ABI; adapters/type projection pending' }));
  return {
    schemaVersion: 1, kind: 'P6-01-native-extension-source-inventory', sourceSha: SOURCE_SHA, sourceTree: '200bd10bb146773516f862a02b8aaebeed163e00', implementationOwner: { model: 'gpt-6.1-sol', reasoningEffort: 'xhigh' },
    authorityPins: [
      { id: 'canonical-baseline', path: 'compatibility/baseline.lock.json', bytes: 59384, sha256: '3d39afb8a23079ae35a123b66dee1bbd291cdf16c2bf30b16ecb3ca923338a1a' },
      { id: 'official-source-inspection', path: 'artifacts/released-baseline/inspection.json', bytes: 41808, sha256: 'c99a5a5db625fc4ee8dbcb2b69b6e803bd3c88943105d6c4a64ae6df757c490b' },
    ],
    evidence: { sourceArchive: { path: 'artifacts/released-baseline/pi-0.99.1-source.tar.gz', bytes: 8646242, sha256: '4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b', prefix: 'pi-0.99.1/', origin: 'Previously acquired official publisher source archive; source comparison qualified in committed release evidence. This lane only reads existing bytes.' }, reader: { path: 'tools/CompatibilityReport/license-evidence.test.mjs', sha256: 'a333dba4449d7e97fc322feb578faf7bb833039dd9970e742076249041d2fccd' }, parserDependency: { path: 'tools/CompatibilityReport/raw-json.mjs', sha256: null }, phasePlans: [{ path: 'docs/plans/06-native-extension-sdk.md', bytes: phase6.length, sha256: hash(phase6) }, { path: 'docs/plans/07-node-bridge.md', bytes: phase7.length, sha256: hash(phase7) }], hashSemantics: 'sha256 of acquired exact LF source bytes; gitBlob is locally computed raw Git SHA-1 blob header+bytes, not a Git invocation. Canonical unchanged files selected from frozen publisher archive.' },
    gate: { P6_01: 'HOLD', P1_04: 'HOLD', P6_G: 'HOLD', P7_G: 'HOLD', approvedNativeAbi: false, implementationAccepted: false, upstreamBehaviorExecuted: false, typeScriptAstClosureProven: false, allEightPhasesComplete: false },
    mechanicalBoundary: { mode: 'bounded-lexical-census', censusFiles: [TYPES], includes: ['Every named top-level interface/type/function declaration and overload in types.ts', 'Every direct member signature of every types.ts interface, including nested inline callback/option object grammar retained verbatim', 'Every export-list name in types.ts and extension index.ts; extension-related package-root re-exports', 'All 41 ExtensionAPI.on event overloads; context inheritance edges and private support declarations', 'Direct import signatures of the nine extension/communication seam files'], excludes: ['TypeScript AST/type-checker completeness', 'Recursive expansion of every imported/transitive model/session/TUI/provider type', 'AST semantics for nested inline properties/type utility expansion or conditional types', 'Every exported method of concrete host classes outside the explicitly inventoried interfaces', 'Runtime module loading, reducer behavior tests, SDK/ABI approval and full node bridge corpus'], limits: 'Comment/string-aware token and balanced delimiter scanner over these exact pinned files only; nested templates/regex literals/general TypeScript grammar are not certified. Unknown census interface member/end grammar fails instead of dropping a row.' },
    sourceFiles: [...sourcePaths].sort().map(path => sourcePin(path, source.get(path))), dependencies, externalPackages,
    declarations, typeReexports: types.exports, extensionBarrelExports: barrel.exports, extensionPackageRootExports: unique(rootExports), surfaces, eventBusMembers: busMembers, contextFacades, documentation, events,
    requiredIntegrationRows: [
      { id: 'native-entry-lifetime', requirement: 'IPiSharpExtension : IAsyncDisposable; InitializeAsync(IExtensionRegistry, CancellationToken); separate operation/session/lifetime cancellation ownership', owner: 'PiSharp.Extensions.Abstractions and Runtime', phases: ['P6-01', 'P6-09'] },
      { id: 'registration-transaction', requirement: 'Owner/registration ID/lifetime for every handler/tool/command/flag/shortcut/provider/MCP/virtual-model/renderer; stage and commit atomically; failure rollback; captured dispatch snapshots unaffected by removal', owner: 'PiSharp.Extensions.Runtime registration coordinator', phases: ['P6-03'] },
      { id: 'trust-loader-discovery', requirement: 'Trust/version/path validation before code activation; no restore during discovery; collectible ALC+one shared Abstractions identity; intentional native manifest/path rules', owner: 'PiSharp.Extensions.Runtime discovery/trust/loader', phases: ['P6-04', 'P6-05'] },
      { id: 'tool-final-policy', requirement: 'Final argument validation/authorization after transforms; nested normal pipeline, parent IDs and bounded depth; progress/result missing/null/clear and structured-content redaction semantics', owner: 'P3 pipeline plus P6 tool/action adapters', phases: ['P6-06'] },
      { id: 'session-generation-state', requirement: 'Branch-sensitive namespaced durable custom state; preserve unknown records; fresh context after replacement; stale actions reject; callbacks outside locks; command-only idle/session/reload', owner: 'P4 coordinator plus P6 context/command host', phases: ['P6-07'] },
      { id: 'ui-provider-mcp-virtual', requirement: 'All UI modes/capabilities and owner disposal; startup/runtime provider override/unregister/restore; MCP and virtual models required wider integration, remain visible until P8 closure', owner: 'P2/P5 hosts plus P6 capability adapters', phases: ['P6-08', 'P8'] },
      { id: 'quiescence-reload', requirement: 'Stop admission/drain/cancel/dispose/detach/invalidate/unload/reactivate; no duplicate generation; cooperative100cycle evidence; hung/rooted plugin restart-required', owner: 'PiSharp.Extensions.Runtime lifetime/reload coordinator', phases: ['P6-09'] },
      { id: 'sdk-release-evidence', requirement: 'Approved API snapshot/consumer/forbidden dependency tests; published SDK/templates/scenarios; per-row P6 report with reviewed deviations and supported platform evidence', owner: 'PiSharp.Extensions.Sdk and release owner', phases: ['P6-10', 'P6-G'] },
    ].map(row => ({ ...row, required: true, nativeBehavior: 'Deferred', fixtureIds: [] })),
    nativeSyntaxRules: [
      { id: 'host-owned-abi', classification: 'IntentionalDifference', approval: 'Pending', rule: 'Versioned net10.0 interfaces and host-owned DTO/schema/JSON/error identifiers; no SDK, concrete SessionManager/ModelRegistry/service container or terminal classes in stable core ABI.' },
      { id: 'immutable-decision-adapter', classification: 'IntentionalDifference', approval: 'Pending', rule: 'Translate JS in-place mutation to typed immutable decisions/field patches per individual reducer; distinguish missing/null/undefined/clear; no generic last-result-wins.' },
      { id: 'async-cancellation-ownership', classification: 'IntentionalDifference', approval: 'Pending', rule: 'Explicit CancellationToken ownership and awaited native barriers where required; preserve source sync getter contract and document actual scheduling differences.' },
      { id: 'rich-js-state-and-ui', classification: 'IntentionalDifference', approval: 'Pending', rule: 'Renderer state/functions/components remain local owner-bound optional TUI contracts; bridge uses opaque callback handles and admits JSON separately; custom terminal parity remains required explicit scope decision.' },
    ],
    bridgePrerequisites: { optional: true, fullApprovedPhase7ScopePreserved: true, P7_01: '8-12 real pinned unmodified extension corpus, source/license/import/API/dependency/scenario evidence, actual upstream+prototype behavior and explicit proceed/narrow/defer decision; module loading alone insufficient.', P7_02: 'Per-export/method TierA/B/C support matrix; unsupported registration diagnostic; no silent omission. This native inventory is input, not that matrix.', remaining: ['P1 source/corpus licenses and P1-04 closure', 'P3 authorized broker and final argument checks', 'P4 immutable branches/session generations', 'P5 mode/UI capabilities', 'P6 approved registries/reducers/ownership/quiescence', 'P7 protocol/full duplex/cancellation/worker/import aliases/sync snapshots/mutation translation/recovery/corpus certification'] },
    gaps: [
      { id: 'P1-04-AST-transitive-census', status: 'OPEN', detail: 'Exact lexical completeness for declared types.ts rows is reviewable; recursively complete public type/AST inventory is not proven.' },
      { id: 'mode-implementation-qualification', status: 'OPEN', detail: 'Complete pinned guides are retained and source-linked; actual UI mode emitters/provider adapters and example scenarios have not been executed or qualified by this inventory.' },
      { id: 'behavior-corpus', status: 'OPEN', detail: 'No upstream extension module/reducer executed in this source inventory; per-reducer valid/no-result/invalid/failure/cancel/composition/snapshot fixtures remain required.' },
      { id: 'native-ABI-review', status: 'OPEN', detail: 'Owner proposals are explicit but native API names/DTO layouts/capability versions and deliberate syntax differences need P6 approval baseline/consumer tests.' },
      { id: 'host-adapter-integration', status: 'OPEN', detail: 'All mandatory rows stay Deferred until P2-5/P6 integration evidence; required UI/provider/MCP/virtual-model closures cannot disappear with a sample subset.' },
      { id: 'node-bridge-feasibility', status: 'OPEN', detail: 'Phase7 remains optional full planned scope; no source-loading or compatibility claim follows from this inventory.' },
    ],
    summary: { typesDeclarationsIncludingOverloads: declarations.length, exportedDeclarationRows: declarations.filter(row => row.exported).length, directInterfaceMemberRows: declarations.reduce((n, row) => n + row.members.length, 0), requiredSurfaceRows: surfaces.length, contextFacadeMemberRows: contextFacades.reduce((n, row) => n + row.members.length, 0), eventNames: events.length, eventBusMemberRows: busMembers.length, sourceFiles: sourcePaths.size, declaredDirectImports: dependencies.length, upstreamModulesExecuted: 0, acceptedBehaviorRows: 0 },
  };
}

export function validateSurface(inventory, source, plans) {
  assert.equal(inventory.kind, 'P6-01-native-extension-source-inventory'); assert.equal(inventory.sourceSha, SOURCE_SHA);
  assert.equal(inventory.gate.approvedNativeAbi, false); assert.equal(inventory.gate.typeScriptAstClosureProven, false); assert.equal(inventory.gate.implementationAccepted, false);
  const rebuilt = buildSurface(source, plans.get('docs/plans/06-native-extension-sdk.md'), plans.get('docs/plans/07-node-bridge.md'));
  rebuilt.evidence.parserDependency.sha256 = inventory.evidence.parserDependency.sha256;
  assert.deepEqual(inventory, rebuilt, 'Source census/evidence/required row classification differs');
  assert.equal(inventory.events.length, 41); assert.equal(new Set(inventory.events.map(row => row.name)).size, 41);
  for (const row of inventory.surfaces) { assert(row.required); assert.equal(row.nativeBehavior, 'Deferred'); assert.deepEqual(row.fixtureIds, []); }
  for (const row of inventory.events) { assert(row.required); assert.equal(row.nativeBehavior, 'Deferred'); assert(row.dispatchSource.path === RUNNER); }
  assert.equal(inventory.declarations.find(row => row.name === 'ExtensionAPI').members.filter(row => row.name === 'registerProvider').length, 2);
  assert.deepEqual(inventory.declarations.find(row => row.name === 'ExtensionToolContext').members.map(row => row.name), ['tools', 'executeTool']);
  assert(inventory.events.find(row => row.name === 'before_provider_headers').reducer.result === 'ignored');
  assert(inventory.events.find(row => row.name === 'tool_result').reducer.behavior.includes('deletes stale structuredContent'));
  assert(inventory.events.find(row => row.name === 'user_bash').reducer.error === 'reported-and-rethrow');
  const text = source.get(TYPES).toString('utf8');
  const headings = [...text.matchAll(/^(export )?(interface|type|function)\s+([A-Za-z_$][\w$]*)/gm)].map(row => ({ name: row[3], kind: row[2], exported: !!row[1], startLine: lineAt(text, row.index) }));
  assert.deepEqual(inventory.declarations.map(row => ({ name: row.name, kind: row.kind, exported: row.exported, startLine: row.source.startLine })), headings, 'Independent line-heading census disagrees');
  for (const declaration of inventory.declarations.filter(row => row.kind === 'interface')) {
    const exact = text.slice(declaration.source.startUtf16, declaration.source.endUtf16);
    const independent = [...exact.matchAll(/^\t(?:readonly\s+)?([A-Za-z_$][\w$]*)(?=\s*(?:[?:<(]))/gm)].map(row => row[1]);
    assert.deepEqual(declaration.members.map(row => row.name), independent, 'Independent direct-member heading census disagrees: ' + declaration.name);
  }
  return true;
}
function confined(path) {
  const target = resolve(repo, path), suffix = relative(repo, target); assert(suffix && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep));
  for (let cursor = target; ; cursor = dirname(cursor)) { assert(!lstatSync(cursor).isSymbolicLink()); if (dirname(cursor) === cursor) break; }
  return readFileSync(target);
}
export async function main(args = process.argv.slice(2)) {
  assert.deepEqual(args, []); const bytes = confined(inventoryPath); assert.equal(hash(bytes), inventorySha256, 'Frozen native surface inventory changed');
  const inventory = JSON.parse(bytes.toString('utf8'));
  const authorities = new Map(); for (const pin of inventory.authorityPins) { const bytes = confined(pin.path); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256); authorities.set(pin.id, JSON.parse(bytes.toString('utf8'))); }
  const baseline = authorities.get('canonical-baseline'), released = authorities.get('official-source-inspection');
  assert.equal(baseline.source.commit, SOURCE_SHA); assert.equal(baseline.source.tree, inventory.sourceTree); assert.equal(released.sourceSha, SOURCE_SHA); assert.equal(released.sourceComparison.comparedRegularFiles, 2093); assert.equal(released.sourceComparison.differingTrackedFiles, 0); assert.equal(released.release.archiveSha256, inventory.evidence.sourceArchive.sha256);
  for (const pin of [inventory.evidence.reader, inventory.evidence.parserDependency]) assert.equal(hash(confined(pin.path)), pin.sha256, 'Inert reader/parser dependency changed');
  const { inspectInertArchive } = await import('../CompatibilityReport/license-evidence.test.mjs');
  const archive = confined(inventory.evidence.sourceArchive.path); assert.equal(archive.length, inventory.evidence.sourceArchive.bytes); assert.equal(hash(archive), inventory.evidence.sourceArchive.sha256);
  const source = inspectInertArchive(archive, inventory.evidence.sourceArchive.prefix), plans = new Map(inventory.evidence.phasePlans.map(row => [row.path, confined(row.path)]));
  for (const row of inventory.sourceFiles) { const bytes = source.get(row.path); assert(bytes); assert.equal(bytes.length, row.bytes); assert.equal(hash(bytes), row.sha256); assert.equal(blob(bytes), row.gitBlob); }
  for (const path of [TYPES, RUNNER, EXT + 'loader.ts']) { const historical = baseline.artifacts.find(row => row.path === path), current = inventory.sourceFiles.find(row => row.path === path); assert(historical); assert.equal(historical.sha256, current.sha256); assert.equal(historical.gitBlob, current.gitBlob); assert.equal(historical.bytes, current.bytes); }
  validateSurface(inventory, source, plans);
  const tests = [{ id: 'frozen-source-byte-and-complete-lexical-census', status: 'pass' }];
  const mutations = [
    ['delete-event', x => x.events.pop()], ['delete-overload', x => x.declarations.find(r => r.name === 'ExtensionAPI').members.pop()],
    ['delete-context-operation', x => x.surfaces.splice(x.surfaces.findIndex(r => r.member === 'executeTool'), 1)],
    ['delete-provider-overload', x => x.declarations.find(r => r.name === 'ExtensionAPI').members.splice(x.declarations.find(r => r.name === 'ExtensionAPI').members.findIndex(r => r.name === 'registerProvider'), 1)],
    ['delete-MCP', x => x.surfaces = x.surfaces.filter(r => r.member !== 'registerMcpServer')], ['delete-virtual-model', x => x.surfaces = x.surfaces.filter(r => r.member !== 'registerVirtualModel')],
    ['delete-UI-overload', x => x.declarations.find(r => r.name === 'ExtensionUIContext').members = x.declarations.find(r => r.name === 'ExtensionUIContext').members.filter(r => r.id !== 'ExtensionUIContext.setWidget#2')],
    ['erase-context-inheritance', x => x.declarations.find(r => r.name === 'ExtensionCommandContext').inherited = null],
    ['mark-sample-behavior-accepted', x => x.surfaces[0].nativeBehavior = 'Accepted'], ['pretend-AST-proof', x => x.gate.typeScriptAstClosureProven = true],
    ['pretend-ABI-approval', x => x.gate.approvedNativeAbi = true], ['generic-last-result-wins', x => x.events.find(r => r.name === 'input').reducer.behavior = 'last result wins'],
    ['header-return-replacement', x => x.events.find(r => r.name === 'before_provider_headers').reducer.result = 'replacement'],
    ['erase-tool-structured-clear', x => x.events.find(r => r.name === 'tool_result').reducer.behavior = 'patch all fields'],
    ['shell-failure-fallback', x => x.events.find(r => r.name === 'user_bash').reducer.error = 'local shell fallback'],
    ['erase-source-hash', x => x.sourceFiles[0].gitBlob = '0'.repeat(40)], ['erase-mode-qualification-gap', x => x.gaps = x.gaps.filter(r => r.id !== 'mode-implementation-qualification')],
    ['erase-session-read-method', x => x.contextFacades[0].members.pop()], ['erase-model-auth-method', x => x.contextFacades[2].members = x.contextFacades[2].members.filter(r => r.name !== 'getApiKeyAndHeaders')],
    ['remove-phase7-corpus-prerequisite', x => x.bridgePrerequisites.P7_01 = 'module load proves compatibility'],
    ['erase-owned-registration-lifetime', x => x.requiredIntegrationRows = x.requiredIntegrationRows.filter(r => r.id !== 'registration-transaction')],
  ];
  for (const [id, mutate] of mutations) { const candidate = structuredClone(inventory); mutate(candidate); assert.throws(() => validateSurface(candidate, source, plans), undefined, id + ' was not detected'); tests.push({ id, status: 'pass' }); }
  const grammar = 'export interface Demo extends Parent { on(event: "one", handler: (x: { value?: string; }) => Promise<void> | void): () => void; on(event: "two", handler: () => void): () => void; readonly value?: { data: string[]; }; }';
  const probe = census(grammar, 'authored-census-probe.ts'); assert.deepEqual(probe.declarations[0].members.map(row => row.id), ['Demo.on#1', 'Demo.on#2', 'Demo.value#1']); assert.equal(probe.declarations[0].inherited, 'Parent'); assert.equal(probe.declarations[0].members[2].optional, true); tests.push({ id: 'authored-nested-signature-overload-census', status: 'pass' });
  assert.throws(() => census('export interface Unsupported { [key: string]: unknown; }', 'authored-unsupported.ts')); tests.push({ id: 'unsupported-member-grammar-fails-explicitly', status: 'pass' });
  process.stdout.write(JSON.stringify({ kind: 'native-extension-surface-inert-census-tests', sourceSha: SOURCE_SHA, scope: 'source-inventory-only; no AST/behavior/ABI acceptance', passed: tests.length, failed: 0, summary: inventory.summary, tests }, null, 2) + '\n');
}
if (process.argv[1] && resolve(process.argv[1]) === own) main().catch(error => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
