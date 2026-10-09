// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (loadExtensionModule,
// getAliases) and packages/coding-agent/src/core/extensions/virtual-modules.ts.
//
// Upstream loads extensions with jiti: TypeScript is transformed (Babel), imports run with CommonJS interop (a named import that a
// module does not export is undefined rather than a link error), `require`, `__dirname` and `__filename` work in every file, and
// Pi's own packages resolve to the running Pi (aliases or virtual modules). The PiSharp bridge does the same with the user's Node:
// module.stripTypeScriptTypes in transform mode for TypeScript, an import rewrite for jiti's interop, and the virtual modules of
// ./virtual (or a real Pi installation named by PISHARP_PI_NODE_MODULES, resolved as upstream's getAliases does).
import fs from 'node:fs';
import path from 'node:path';
import module from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const TS = /\.(?:ts|mts|cts|tsx)$/i;

/** Specifier -> absolute file of the module that provides it. */
export function virtualModuleTable() {
  const table = new Map();
  const realRoot = process.env.PISHARP_PI_NODE_MODULES;
  if (realRoot && fs.existsSync(path.join(realRoot, '@earendil-works', 'pi-coding-agent', 'package.json'))) {
    // getAliases over an installed Pi: the package entry points of the real modules.
    const req = module.createRequire(path.join(realRoot, 'noop.js'));
    const tryResolve = (spec) => { try { return req.resolve(spec); } catch { return undefined; } };
    for (const [spec, target] of Object.entries({
      'typebox': 'typebox', 'typebox/compile': 'typebox/compile', 'typebox/value': 'typebox/value',
      '@sinclair/typebox': 'typebox', '@sinclair/typebox/compile': 'typebox/compile', '@sinclair/typebox/value': 'typebox/value',
      '@earendil-works/pi-coding-agent': '@earendil-works/pi-coding-agent', '@earendil-works/pi-agent-core': '@earendil-works/pi-agent-core',
      '@earendil-works/pi-tui': '@earendil-works/pi-tui', '@earendil-works/pi-ai': '@earendil-works/pi-ai/compat',
      '@earendil-works/pi-ai/compat': '@earendil-works/pi-ai/compat', '@earendil-works/pi-ai/oauth': '@earendil-works/pi-ai/oauth',
      '@earendil-works/pi-ai/providers/all': '@earendil-works/pi-ai/providers/all',
    })) {
      const resolved = tryResolve(target);
      if (resolved) { table.set(spec, resolved); if (spec.startsWith('@earendil-works/')) table.set(spec.replace('@earendil-works/', '@mariozechner/'), resolved); }
    }
    if (table.size > 0) return table;
  }
  const indexPath = path.join(here, 'virtual', 'index.json');
  if (!fs.existsSync(indexPath)) return table;
  const index = JSON.parse(fs.readFileSync(indexPath, 'utf8'));
  for (const [spec, file] of Object.entries(index)) table.set(spec, path.join(here, 'virtual', file));
  return table;
}

const table = virtualModuleTable();

function exists(file) { try { return fs.statSync(file).isFile(); } catch { return false; } }

/** Extension-less and `.js`-for-`.ts` relative imports, as jiti resolves them. */
function resolveRelative(specifier, parentURL) {
  if (!parentURL?.startsWith('file:')) return undefined;
  if (!(specifier.startsWith('./') || specifier.startsWith('../') || specifier.startsWith('/') || /^[A-Za-z]:[\\/]/.test(specifier) || specifier.startsWith('file:'))) return undefined;
  const base = specifier.startsWith('file:') ? fileURLToPath(specifier) : path.resolve(path.dirname(fileURLToPath(parentURL)), specifier);
  if (exists(base)) return base;
  const candidates = [];
  const ext = path.extname(base);
  if (ext === '.js' || ext === '.mjs' || ext === '.cjs') {
    const stem = base.slice(0, -ext.length);
    candidates.push(stem + (ext === '.js' ? '.ts' : ext === '.mjs' ? '.mts' : '.cts'), stem + '.tsx');
  }
  for (const suffix of ['.ts', '.tsx', '.mts', '.cts', '.js', '.mjs', '.cjs', '.json']) candidates.push(base + suffix);
  for (const index of ['index.ts', 'index.tsx', 'index.mts', 'index.js', 'index.mjs', 'index.cjs']) candidates.push(path.join(base, index));
  return candidates.find(exists);
}

function then(value, next) { return value && typeof value.then === 'function' ? value.then(next) : next(value); }

export function resolve(specifier, context, nextResolve) {
  const virtual = table.get(specifier);
  if (virtual) return { url: pathToFileURL(virtual).href, shortCircuit: true };
  const relative = resolveRelative(specifier, context.parentURL);
  if (relative) return { url: pathToFileURL(relative).href, shortCircuit: true };
  return nextResolve(specifier, context);
}

/** Every `import … from "x"` with bindings becomes a namespace import plus bindings read through jiti-style interop. */
export function rewriteImports(code) {
  let counter = 0;
  const pattern = /(^|[;\n])([ \t]*)import\s+(?!type\b)([\w$]+\s*,\s*\{[\s\S]*?\}|[\w$]+\s*,\s*\*\s*as\s+[\w$]+|\{[\s\S]*?\}|[\w$]+|\*\s*as\s+[\w$]+)\s*from\s*(['"])([^'"\n]+)\4\s*(?:with\s*\{[^}]*\}\s*)?;?/g;
  let used = false;
  const out = code.replace(pattern, (all, lead, indent, clause, _q, source) => {
    const ns = `__pisharp_import_${counter++}`;
    const parts = []; let named = null; let def = null; let star = null;
    const braces = clause.indexOf('{');
    if (braces >= 0) {
      named = clause.slice(braces + 1, clause.lastIndexOf('}'));
      const head = clause.slice(0, braces).replace(/,\s*$/, '').trim();
      if (head) def = head;
    } else if (/^\*\s*as\s+/.test(clause)) star = clause.replace(/^\*\s*as\s+/, '').trim();
    else if (clause.includes(',')) { const [d, s] = clause.split(','); def = d.trim(); star = s.replace(/^\s*\*\s*as\s+/, '').trim(); }
    else def = clause.trim();
    if (star && !def && !named) return all; // A plain namespace import needs no interop.
    used = true;
    parts.push(`import * as ${ns} from ${JSON.stringify(source)};`);
    if (def) parts.push(`const ${def} = __pisharp_default(${ns});`);
    if (star) parts.push(`const ${star} = ${ns};`);
    if (named) {
      const bindings = named.split(',').map(item => item.trim()).filter(Boolean).filter(item => !/^type\s/.test(item)).map(item => {
        const match = /^([\w$]+|"[^"]*"|'[^']*')(?:\s+as\s+([\w$]+))?$/.exec(item);
        if (!match) return null;
        const imported = match[1].replace(/^['"]|['"]$/g, ''); const local = match[2] ?? imported;
        return `${local}: ${JSON.stringify(imported)}`;
      }).filter(Boolean);
      if (bindings.length) {
        const locals = bindings.map(b => b.split(':')[0]);
        const props = bindings.map(b => { const [local, imported] = b.split(': '); return `${imported}: ${local}`; });
        parts.push(`const { ${props.join(', ')} } = __pisharp_named(${ns});`);
        void locals;
      }
    }
    return lead + indent + parts.join(' ');
  });
  if (!used) return code;
  return out + '\nfunction __pisharp_named(ns) { const d = ns.default; if (d === null || (typeof d !== "object" && typeof d !== "function")) return ns; return new Proxy(ns, { get(target, key) { return key in target ? target[key] : d[key]; } }); }\n' +
    'function __pisharp_default(ns) { return "default" in ns ? ns.default : ns; }\n';
}

/** jiti gives every module `require`, `__filename` and `__dirname`; ESM output from the transform gets them when it uses them. */
function addCommonJsGlobals(code, filename) {
  const declares = (name) => new RegExp(`(?:\\b(?:const|let|var|function|class)\\s+${name}\\b|import[^;]*\\b${name}\\b)`).test(code);
  const prefix = [];
  if (/\brequire\s*[(.]/.test(code) && !declares('require')) prefix.push('import { createRequire as __pisharp_createRequire } from "node:module"; const require = __pisharp_createRequire(import.meta.url);');
  if (/\b__filename\b/.test(code) && !declares('__filename')) prefix.push(`const __filename = ${JSON.stringify(filename)};`);
  if (/\b__dirname\b/.test(code) && !declares('__dirname')) prefix.push(`const __dirname = ${JSON.stringify(path.dirname(filename))};`);
  return prefix.length ? prefix.join(' ') + '\n' + code : code;
}

function isModuleSyntax(code) { return /(^|[\n;])\s*(import\s*[\w{*'"]|export\s+[\w{*]|export\s+default\b)/.test(code) || /\bimport\.meta\b/.test(code); }

export function transformTypeScript(source, filename) {
  if (/\.tsx$/i.test(filename)) throw new Error(`TSX extensions are not supported by the PiSharp Node bridge: ${filename}`);
  if (typeof module.stripTypeScriptTypes !== 'function')
    throw new Error(`TypeScript extensions need Node.js 22.13 or later (module.stripTypeScriptTypes); found ${process.version}: ${filename}`);
  let code = source.charCodeAt(0) === 0xfeff ? source.slice(1) : source;
  const shebang = code.startsWith('#!') ? code.slice(0, code.indexOf('\n') + 1) : '';
  if (shebang) code = code.slice(shebang.length);
  code = module.stripTypeScriptTypes(code, { mode: 'transform', sourceUrl: pathToFileURL(filename).href });
  const commonjs = /\.cts$/i.test(filename) || (!/\.mts$/i.test(filename) && !isModuleSyntax(code));
  if (commonjs) return { format: 'commonjs', source: code };
  code = addCommonJsGlobals(rewriteImports(code), filename);
  return { format: 'module', source: code };
}

export function load(url, context, nextLoad) {
  if (url.startsWith('file:')) {
    const filename = fileURLToPath(url);
    if (TS.test(filename)) {
      const { format, source } = transformTypeScript(fs.readFileSync(filename, 'utf8'), filename);
      return { format, source, shortCircuit: true };
    }
  }
  return nextLoad(url, context);
}

let installed = false;
/** Installs the hooks in this thread (module.registerHooks, Node 22.15+) or the loader thread (module.register). */
export async function installHooks() {
  if (installed) return; installed = true;
  if (typeof module.registerHooks === 'function') {
    module.registerHooks({
      resolve: (specifier, context, nextResolve) => resolve(specifier, context, nextResolve),
      load: (url, context, nextLoad) => {
        const result = load(url, context, nextLoad);
        // CommonJS TypeScript compiled here: Node needs the source as a string.
        return result;
      },
    });
    return;
  }
  module.register(pathToFileURL(path.join(here, 'loader-thread.mjs')).href);
}
void then;
