#!/usr/bin/env node
// Re-pins upstream source citations from one Pi release to the next, mechanically and only where it is safe.
//
// A citation is an occurrence of the old commit in a tracked file, usually a header such as
//   // Pi <commit> (MIT): packages/<pkg>/src/<file>.ts (names), packages/<pkg>/src/<other>.ts.
// For each occurrence the tool reads the upstream paths it names (the rest of the line and its
// continuation comment lines; or, for "<path> at <commit>" forms, the text before it), expands
// {a,b} groups, resolves package-relative paths, and compares each path's git object id at the two
// tags. Only when every named path is byte-identical (same blob or tree id) is that occurrence
// rewritten to the new commit. Anything else stays on the old commit and goes to the work list:
// changed, deleted or renamed upstream files, and citations whose paths could not be parsed.
// Historical records (release notes, decisions, earlier sync records, fixtures, inventories generated
// for the old target, captured logs) are never rewritten; docs that describe the published release
// wait for the release; known non-header pins (constants, generated-module commits, JSON
// inventories) are reported, not rewritten.
//
// Usage: node repin-sources.mjs --upstream <Pi git repo containing both tags> [--from v1.1.0] [--to v1.2.0] [--report <path>]
//          [--worklist compatibility/sync-1.2.0-worklist.json] [--applicability compatibility/sync-1.2.0-applicability.json]
//          [--write]
// Without --write it only reports. The work list keeps any top-level keys it does not generate
// (for example the authored nonHeaderPins section).
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const self = "tools/CompatibilityReport/repin-sources.mjs";
const args = { from: "v1.1.0", to: "v1.2.0", worklist: "compatibility/sync-1.2.0-worklist.json", applicability: "compatibility/sync-1.2.0-applicability.json" };
for (let index = 2; index < process.argv.length; index++) {
  const name = process.argv[index];
  if (!name.startsWith("--")) throw new Error(`Unexpected argument ${name}`);
  if (name === "--write") args.write = true; else args[name.slice(2)] = process.argv[++index];
}
if (!args.upstream) throw new Error("--upstream <Pi git repository containing both tags> is required");

const upstreamGit = (...command) => execFileSync("git", ["-C", resolve(args.upstream), ...command], { maxBuffer: 1 << 30 }).toString();
const repoGit = (...command) => execFileSync("git", ["-C", repo, ...command], { maxBuffer: 1 << 30 }).toString();
const OLD = upstreamGit("rev-parse", `${args.from}^{commit}`).trim();
const NEW = upstreamGit("rev-parse", `${args.to}^{commit}`).trim();
const SHA = /\b[0-9a-f]{40}\b/;

// ------------------------------------------------------------------ upstream object comparison
const objectCache = new Map();
function objectIds(paths) {
  const wanted = [...new Set(paths)].filter(path => !objectCache.has(path));
  if (wanted.length > 0) {
    const input = wanted.flatMap(path => [`${OLD}:${path}`, `${NEW}:${path}`]).join("\n") + "\n";
    const output = execFileSync("git", ["-C", resolve(args.upstream), "cat-file", "--batch-check=%(objectname) %(objecttype)"], { input, maxBuffer: 1 << 30 }).toString().trim().split("\n");
    wanted.forEach((path, index) => {
      const parse = line => line.endsWith(" missing") ? null : { id: line.split(" ")[0], type: line.split(" ")[1] };
      objectCache.set(path, { old: parse(output[index * 2]), new: parse(output[index * 2 + 1]) });
    });
  }
  return new Map(paths.map(path => [path, objectCache.get(path)]));
}
const renames = new Map(upstreamGit("diff", "--name-status", "-M", OLD, NEW).trim().split("\n")
  .map(line => line.split("\t")).filter(([status]) => status.startsWith("R")).map(([, from, to]) => [from, to]));
const numstatCache = new Map();
function linesChanged(path) {
  if (!numstatCache.has(path)) {
    const rows = upstreamGit("diff", "--numstat", OLD, NEW, "--", path).trim().split("\n").filter(Boolean)
      .map(line => line.split("\t")).map(([added, removed]) => (Number(added) || 0) + (Number(removed) || 0));
    numstatCache.set(path, rows.reduce((sum, value) => sum + value, 0));
  }
  return numstatCache.get(path);
}

// ------------------------------------------------------------------ file classification
const historical = [
  [/^docs\/release-notes\//, "release notes record what each release was built from"],
  [/^website\/src\/content\/changelog\//, "changelog entries record what each release was built from"],
  [/(^|\/)CHANGELOG\.md$/, "changelog history"],
  [/^docs\/decisions\/0002-pi-1\.1\.0-sync\.md$/, "the v1.1.0 sync decision"],
  [/^docs\/decisions\/0006-pi-1\.2\.0-sync\.md$/, "names the commit the v1.2.0 sync starts from"],
  [/^compatibility\/sync-1\.1\.0-applicability\.json$/, "the v1.1.0 sync record"],
  [/^compatibility\/sync-1\.2\.0-(applicability|worklist)\.json$/, "names the commit the v1.2.0 sync starts from"],
  [/^compatibility\/target\.lock\.json$/, "previousTarget names the v1.1.0 lock"],
  [/^compatibility\/target\.v1\.1\.0\.lock\.json$/, "the preserved v1.1.0 target lock"],
  [/^compatibility\/.*\.v1\.1\.0(\.overlay)?\.json$/, "inventory generated for the v1.1.0 target (validated against target.v1.1.0.lock.json)"],
  [/^compatibility\/.*\.v1\.2\.0\.json$/, "inventory generated for the v1.2.0 target; its v1.1.0 commit is refresh provenance (the previous inventory and each row's previous source) and the anchors of entries removed upstream at v1.2.0"],
  [/^compatibility\/public-release\.json$/, "previousReleases record the upstream commit each published release tracked"],
  [/^docs\/compatibility\/baseline\.md$/, "describes the previous target"],
  [/^docs\/plans\/(full-parity-v1\.1\.0\.md|post-port-pi-v1-upgrade\.md)$/, "the v1.1.0 plans"],
  [/^docs\/contracts\//, "contract records name the release each adapter was re-ported from"],
  [/^fixtures\//, "historical fixtures"],
  [/\.log$/, "captured log"],
  [new RegExp(`^${self.replace(/[.]/g, "\\.")}$`), "this tool"],
];
const releaseDocs = [
  [/^README\.md$/, "describes the published release's baseline; update with the 1.2.0 release"],
  [/^website\/src\/content\/docs\//, "describes the published release's baseline; update with the 1.2.0 release"],
];
// Pins that are not citation headers: reported with their own handling, never rewritten here.
const nonHeaderFiles = [
  [/^tools\/NodeBridge\/pi-host\/virtual\/port-upstream\.mjs$/, "node-bridge", "generator COMMIT constant; regenerate the compatibility modules against the new tag"],
  [/^tools\/NodeBridge\/pi-host\/virtual\/upstream-exports\.json$/, "node-bridge", "export snapshot of the old tag; regenerated with port-upstream.mjs"],
  [/(^|\/)source-inventory\.json$|^tests\/PiSharp\.Tui\.Keybindings\.Tests\/fixtures\/source-defaults-inventory\.json$/, "inventory", "JSON inventory pinning upstream file hashes at the old commit; regenerate it when its area is re-verified"],
];
const nonHeaderLines = [
  [/commit: "[0-9a-f]{40}", exports/, "node-bridge", "selftest writes the upstream-exports.json snapshot with the generator's commit; move it with the regenerated modules"],
  [/const string (Upstream|SourceSha) = "[0-9a-f]{40}"|sourceSha = "[0-9a-f]{40}"/, "suite-label", "test-report baseline label; move it when the suite's area is re-ported and the suite passes against the new baseline"],
];

// ------------------------------------------------------------------ citation parsing
const PACKAGES = "agent|ai|tui|codemode|mcp|coding-agent|telemetry|chord|durable|env|client|server|protocol";
const PACKAGE_PATH = new RegExp(`(?:packages/)?(?:${PACKAGES})/(?:src|docs|scripts|schemas|examples|install-lock|test|package\\.json|CHANGELOG\\.md|README\\.md)(?:[\\w.\\-/*]|\\{[^}\\s]*\\})*`, "g");
const RELATIVE_PATH = /(?<![\w./{}*-])(?:[\w.*-]|\{[^}\s]*\}|\/)+\.(?:ts|tsx|json|md)\b/g;
const COMMENT = /^\s*(\/\/\/?|#|\*|<!--|\/\*\*?)/;

function expandBraces(path) {
  const match = /\{([^{}]*)\}/.exec(path);
  if (!match) return [path];
  return match[1].split(",").flatMap(part => expandBraces(path.slice(0, match.index) + part + path.slice(match.index + match[0].length)));
}
function normalize(path) {
  let cleaned = path.replace(/#L\d+(-L?\d+)?$/, "").replace(/[.,;:)]+$/, "").replace(/\/$/, "");
  if (!cleaned.startsWith("packages/") && new RegExp(`^(?:${PACKAGES})/`).test(cleaned)) cleaned = "packages/" + cleaned;
  if (cleaned.includes("*")) cleaned = cleaned.slice(0, cleaned.indexOf("*")).replace(/\/[^/]*$/, "");
  return cleaned;
}
// Upstream paths named by a piece of citation text, in order. A package-relative name (cli/args.ts,
// kimi-coding.ts, providers/*.ts) resolves against the directory of the path named before it, then
// that path's package src, then packages/coding-agent/src, then a unique source file at the old tag
// ending with the name. Names that are PiSharp paths, or bare file names that are no upstream
// source (auth.json), are not citations. Anything else that does not resolve is returned as
// unresolved, which keeps the citation from being re-pinned.
let sourceList;
const oldSources = () => sourceList ??= upstreamGit("ls-tree", "-r", "--name-only", OLD, "packages").trim().split("\n")
  .filter(path => /\/(src|docs)\//.test(path) && !/\/(test|examples)\//.test(path));
const existsOld = path => Boolean(objectIds([path]).get(path).old);
function pathsIn(text) {
  const found = [], unresolved = [];
  const packageMatches = [...text.matchAll(PACKAGE_PATH)].map(match => ({ index: match.index, text: match[0], package: true }));
  const masked = packageMatches.reduce((value, match) => value.slice(0, match.index) + " ".repeat(match.text.length) + value.slice(match.index + match.text.length), text);
  const relativeMatches = [...masked.matchAll(RELATIVE_PATH)].map(match => ({ index: match.index, text: match[0], package: false }));
  let last;
  for (const token of [...packageMatches, ...relativeMatches].sort((a, b) => a.index - b.index)) {
    for (const name of expandBraces(token.text)) {
      if (token.package) { found.push(last = normalize(name)); continue; }
      const relative = normalize(name);
      if (relative === "" || existsSync(resolve(repo, relative))) continue;
      const roots = last ? [last.replace(/\/[^/]*$/, ""), last.match(new RegExp(`^packages/(?:${PACKAGES})/src`))?.[0], last.match(new RegExp(`^packages/(?:${PACKAGES})`))?.[0]] : [];
      const hit = [...roots, "packages/coding-agent/src"].filter(Boolean).map(root => `${root}/${relative}`).find(existsOld);
      const suffixed = hit ? [] : oldSources().filter(path => path.endsWith("/" + relative));
      const resolved = hit ?? (suffixed.length === 1 ? suffixed[0] : undefined);
      if (resolved) found.push(last = resolved);
      else if (relative.includes("/") || suffixed.length > 1) unresolved.push(name);
    }
  }
  return { paths: [...new Set(found)], unresolved };
}

function citationText(lines, index, column) {
  const line = lines[index];
  const prefix = line.match(COMMENT)?.[1];
  const after = [line.slice(column + 40)];
  // Continuation: the rest of the comment block (same prefix, not blank), until another commit appears, at most 10 lines.
  // Headers often go on with "Also ..." after a full stop, so a sentence end does not end the citation.
  if (prefix) {
    for (let next = index + 1; next < lines.length && next <= index + 10; next++) {
      const text = lines[next];
      if (text.match(COMMENT)?.[1] !== prefix || text.replace(COMMENT, "").trim() === "" || SHA.test(text)) break;
      after.push(text.replace(COMMENT, ""));
    }
  }
  const before = [line.slice(0, column)];
  if (prefix) {
    for (let previous = index - 1; previous >= 0 && previous >= index - 6; previous--) {
      const text = lines[previous];
      if (text.match(COMMENT)?.[1] !== prefix || /\.\s*$/.test(text) || SHA.test(text)) break;
      before.unshift(text.replace(COMMENT, ""));
    }
  }
  return { after: after.join(" "), before: before.join(" ") };
}

// ------------------------------------------------------------------ scan
const files = repoGit("grep", "-l", "-I", OLD).trim().split("\n").filter(Boolean).sort();
const applicability = existsSync(resolve(repo, args.applicability)) ? JSON.parse(readFileSync(resolve(repo, args.applicability), "utf8")) : { rows: [] };
const rowsFor = path => applicability.rows.filter(row => row.upstream.some(entry => {
  const pattern = entry.replace(/\/\*\*$/, "").replace(/\*.*$/, "");
  return entry === path || (entry.includes("*") && path.startsWith(pattern)) || path.startsWith(entry + "/") || entry.startsWith(path + "/");
}));
const areaOf = file => [
  [/NodeBridge|Compatibility\.Node|Extensions\/Pi\/PiNode|Extensions\/Pi\/PiExtensionHost/, "node-bridge"],
  [/Codemode/, "codemode"], [/Mcp/, "mcp"], [/PiSharp\.Tui|Interactive|HljsGrammars|GrokMermaid/, "tui"],
  [/PiSharp\.AI|Models|Catalog|Provider|Anthropic|Mistral|Azure|Google|OpenAI/, "ai"],
  [/PiSharp\.Tools|ToolSearch/, "tools"], [/Commands|PiSharp\.Cli\/Pi\//, "coding-agent-cli"], [/docs\//, "docs"],
].find(([pattern]) => pattern.test(file))?.[1] ?? "coding-agent-core";

const report = { repinned: [], pending: [], unparsed: [], historical: [], releaseDocs: [], nonHeader: [] };
const writes = [];
for (const file of files) {
  const history = historical.find(([pattern]) => pattern.test(file));
  const raw = readFileSync(resolve(repo, file), "utf8");
  const occurrences = raw.split(OLD).length - 1;
  if (history) { report.historical.push({ file, occurrences, reason: history[1] }); continue; }
  const docs = releaseDocs.find(([pattern]) => pattern.test(file));
  if (docs) { report.releaseDocs.push({ file, occurrences, reason: docs[1] }); continue; }
  const special = nonHeaderFiles.find(([pattern]) => pattern.test(file));
  if (special) {
    let paths = [];
    if (special[1] === "inventory") {
      paths = [...raw.matchAll(/"(?:path|relative)"\s*:\s*"(packages\/[^"]+)"/g)].map(match => match[1]);
      paths = [...new Set(paths)];
    }
    const ids = objectIds(paths);
    report.nonHeader.push({ file, area: special[1] === "inventory" ? areaOf(file) : special[1], kind: special[1], occurrences, handling: special[2],
      ...(paths.length ? { upstream: paths.map(path => withRename({ path, status: status(path, ids.get(path)) })) } : {}) });
    continue;
  }

  const eol = raw.includes("\r\n") ? "\r\n" : "\n";
  const lines = raw.split(eol);
  const fileResult = { citations: [], repinned: 0, paths: new Set() };
  for (let index = 0; index < lines.length; index++) {
    let column = lines[index].indexOf(OLD);
    while (column >= 0) {
      const label = nonHeaderLines.find(([pattern]) => pattern.test(lines[index]));
      if (label) {
        report.nonHeader.push({ file, area: label[1] === "suite-label" ? areaOf(file) : label[1], kind: label[1], line: index + 1, handling: label[2] });
      } else {
        const text = citationText(lines, index, column);
        let parsed = pathsIn(text.after);
        if (parsed.paths.length === 0) parsed = pathsIn(text.before);
        if (parsed.paths.length === 0) {
          report.unparsed.push({ file, line: index + 1, text: lines[index].trim().slice(0, 200) });
        } else {
          const ids = objectIds(parsed.paths);
          const statuses = parsed.paths.map(path => withRename({ path, status: status(path, ids.get(path)) }));
          const invalid = statuses.filter(entry => entry.status === "not-in-old").map(entry => entry.path).concat(parsed.unresolved);
          const changedAny = statuses.some(entry => entry.status !== "unchanged" && entry.status !== "not-in-old");
          if (invalid.length > 0 && !changedAny) {
            // Every resolved path is unchanged, but some named path could not be resolved: check by hand.
            report.unparsed.push({ file, line: index + 1, text: lines[index].trim().slice(0, 200), note: "unresolved: " + invalid.join(", "),
              ...(statuses.some(entry => entry.status === "unchanged") ? { unchanged: statuses.filter(entry => entry.status === "unchanged").map(entry => entry.path) } : {}) });
          } else if (statuses.every(entry => entry.status === "unchanged")) {
            lines[index] = lines[index].slice(0, column) + NEW + lines[index].slice(column + 40);
            fileResult.repinned++;
            for (const entry of statuses) fileResult.paths.add(entry.path);
          } else {
            fileResult.citations.push({ line: index + 1, upstream: statuses.filter(entry => entry.status !== "unchanged") , unchanged: statuses.filter(entry => entry.status === "unchanged").map(entry => entry.path) });
          }
        }
      }
      column = lines[index].indexOf(OLD, column + 40);
    }
  }
  if (fileResult.repinned > 0) {
    report.repinned.push({ file, citations: fileResult.repinned, upstream: [...fileResult.paths].sort() });
    writes.push([file, lines.join(eol)]);
  }
  if (fileResult.citations.length > 0) {
    const changed = new Map();
    for (const citation of fileResult.citations) for (const entry of citation.upstream) changed.set(entry.path, entry);
    const upstream = [...changed.values()].map(entry => ({ ...entry, linesChanged: entry.status === "changed" ? linesChanged(entry.path) : undefined }));
    const rows = [...new Set(upstream.flatMap(entry => rowsFor(entry.renamedTo ?? entry.path).concat(rowsFor(entry.path)).map(row => row.id)))];
    const areas = [...new Set(applicability.rows.filter(row => rows.includes(row.id)).map(row => row.area))];
    report.pending.push({ file, area: areas.length === 1 ? areas[0] : areaOf(file), ...(areas.length > 1 ? { areas } : {}),
      citations: fileResult.citations.map(citation => ({ line: citation.line, changed: citation.upstream.map(entry => entry.path), ...(citation.unchanged.length ? { unchanged: citation.unchanged } : {}) })),
      upstream, changeSize: upstream.reduce((sum, entry) => sum + (entry.linesChanged ?? 0), 0),
      applicabilityRows: rows });
    // A changed upstream file that no applicability row names is a gap in the table.
    const unmapped = upstream.filter(entry => entry.status !== "not-in-old" && rowsFor(entry.path).length === 0).map(entry => entry.path);
    if (unmapped.length > 0) report.pending.at(-1).unmappedUpstream = unmapped;
  }
}

function withRename(entry) { return entry.status === "renamed" ? { ...entry, renamedTo: renames.get(entry.path) } : entry; }
function status(path, ids) {
  if (!ids?.old) return "not-in-old";
  if (!ids.new) return renames.has(path) ? "renamed" : "deleted";
  return ids.old.id === ids.new.id ? "unchanged" : "changed";
}

if (args.write) for (const [file, text] of writes) writeFileSync(resolve(repo, file), text);

// ------------------------------------------------------------------ work list
// A later run (after a port moved more citations, or after --write) keeps the citations earlier runs
// re-pinned: they no longer contain the old commit, so this run cannot see them.
const worklistPath = resolve(repo, args.worklist);
const kept = existsSync(worklistPath) ? JSON.parse(readFileSync(worklistPath, "utf8")) : {};
if (kept.from?.commit === OLD && kept.to?.commit === NEW)
  for (const entry of kept.repinned ?? []) {
    const current = report.repinned.find(item => item.file === entry.file);
    if (!current) report.repinned.push(entry);
    else if (current !== entry) { current.citations += entry.citations; current.upstream = [...new Set([...current.upstream, ...entry.upstream])].sort(); }
  }
report.repinned.sort((a, b) => a.file.localeCompare(b.file));
const count = items => items.reduce((sum, item) => sum + (item.citations ?? item.occurrences ?? 1), 0);
const byArea = {};
for (const entry of report.pending) {
  const area = (byArea[entry.area] ??= { files: 0, changeSize: 0 });
  area.files++; area.changeSize += entry.changeSize;
}
const generated = {
  schemaVersion: 1,
  generatedBy: `node ${self} --upstream <Pi repo> --from ${args.from} --to ${args.to}${args.write ? " --write" : ""}`,
  decision: "docs/decisions/0006-pi-1.2.0-sync.md",
  from: { tag: args.from, commit: OLD },
  to: { tag: args.to, commit: NEW },
  rule: "A citation moves to the new commit only when every upstream path it names has the same git object id at both tags. The rest stay on the old commit until their area is re-ported, then move with the port.",
  summary: {
    filesWithOldCommit: files.length,
    citations: {
      repinned: count(report.repinned),
      pending: report.pending.reduce((sum, entry) => sum + entry.citations.length, 0),
      unparsed: report.unparsed.length,
      nonHeader: report.nonHeader.reduce((sum, entry) => sum + (entry.occurrences ?? 1), 0),
      excludedHistorical: count(report.historical),
      releaseDocs: count(report.releaseDocs),
    },
    files: { repinned: report.repinned.length, pending: report.pending.length, historical: report.historical.length, releaseDocs: report.releaseDocs.length, nonHeader: new Set(report.nonHeader.map(entry => entry.file)).size },
    pendingByArea: Object.fromEntries(Object.entries(byArea).sort(([a], [b]) => a.localeCompare(b))),
  },
  pending: report.pending.sort((a, b) => a.area.localeCompare(b.area) || b.changeSize - a.changeSize || a.file.localeCompare(b.file)),
  unparsed: report.unparsed,
  nonHeaderCitations: report.nonHeader,
  releaseDocs: report.releaseDocs,
  excludedHistorical: report.historical,
  repinned: report.repinned,
};
const merged = { ...generated };
for (const [key, value] of Object.entries(kept)) if (!(key in generated)) merged[key] = value;
if (args.write) writeFileSync(worklistPath, JSON.stringify(merged, null, 2) + "\n");
if (args.report) writeFileSync(resolve(args.report), JSON.stringify(merged, null, 2) + "\n");
console.log(JSON.stringify(generated.summary, null, 2));
