#!/usr/bin/env node
// Source-level re-freeze of the target lock (compatibility/target.lock.json) against a clean upstream checkout.
// Carries the previous lock's source-file paths forward, drops files the target removed, adds
// --add paths, and hashes every row under the unchanged hashPolicy. Never touches fixtures.
// Usage: node refreeze-baseline.mjs --upstream <checkout> --tag <vX.Y.Z> --previous <lock>
//          --previous-path <repo path of preserved lock> [--add a,b,c] --out <new lock>
//          [--decision <repo path of the decision record>]
//          [--evidence <evidence lock> --evidence-path <repo path of the evidence lock>]
// --previous supplies the carried-forward paths, hashPolicy, runtime and gaps. previousBaseline names the
// evidence lock that historical fixtures stay validated against: --evidence when given (a re-freeze of an
// earlier target lock, such as v1.1.0 -> v1.2.0), else --previous (the first re-freeze, v0.99.1 -> v1.1.0).
// With --evidence, the previous target lock is recorded as previousTarget.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) =>
  value.startsWith("--") ? [...pairs, [value.slice(2), all[index + 1]]] : pairs, []));
for (const name of ["upstream", "tag", "previous", "previous-path", "out"])
  if (!args[name]) throw new Error(`--${name} is required`);

const upstream = resolve(args.upstream);
const git = (...command) => execFileSync("git", ["-C", upstream, ...command], { maxBuffer: 1 << 30 });
const text = (...command) => git(...command).toString().trim();
const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");

const commit = text("rev-parse", "HEAD");
if (text("status", "--porcelain", "--untracked-files=all") !== "") throw new Error("Upstream checkout is not clean.");
if (text("rev-parse", `${args.tag}^{commit}`) !== commit) throw new Error(`${args.tag} does not resolve to HEAD ${commit}.`);
const tree = text("rev-parse", `${commit}^{tree}`);

const previous = JSON.parse(readFileSync(resolve(args.previous), "utf8"));
if (Boolean(args.evidence) !== Boolean(args["evidence-path"])) throw new Error("--evidence and --evidence-path go together");
const evidence = args.evidence ? JSON.parse(readFileSync(resolve(args.evidence), "utf8")) : previous;
const evidencePath = args["evidence-path"] ?? args["previous-path"];
const decision = args.decision ?? "docs/decisions/0002-pi-1.1.0-sync.md";
const acquiredAt = new Date().toISOString().slice(0, 10);
const origin = path => `https://github.com/earendil-works/pi/blob/${commit}/${path}`;
const exists = path => { try { execFileSync("git", ["-C", upstream, "cat-file", "-e", `${commit}:${path}`], { stdio: "ignore" }); return true; } catch { return false; } };

const previousRows = new Map(previous.artifacts.filter(row => row.kind === "source-file").map(row => [row.path, row]));
const added = (args.add ?? "").split(",").map(path => path.trim()).filter(Boolean);
const paths = [...previousRows.keys(), ...added.filter(path => !previousRows.has(path))];
const summary = { unchanged: [], changed: [], added: [], removed: [] };
const rows = [];
for (const path of paths) {
  if (!exists(path)) { summary.removed.push(path); continue; }
  const blob = git("show", `${commit}:${path}`);
  const checkout = readFileSync(resolve(upstream, path));
  const row = { kind: "source-file", path, origin: origin(path), acquiredAt, gitBlob: text("rev-parse", `${commit}:${path}`),
    sha256: sha256(blob), bytes: blob.length, checkoutSha256: sha256(checkout), checkoutBytes: checkout.length };
  const before = previousRows.get(path);
  summary[!before ? "added" : before.sha256 === row.sha256 ? "unchanged" : "changed"].push(path);
  rows.push(row);
}

const archive = git("archive", "--format=tar", commit);
const previousArchive = previous.artifacts.find(row => row.kind === "source-archive");
const version = path => JSON.parse(git("show", `${commit}:${path}`).toString()).version;

const lock = {
  schemaVersion: 1,
  status: `Source-level re-freeze per ${decision}; Phase 1 gate open`,
  source: { repository: previous.source.repository, tag: args.tag, commit, tree, tagResolvedCommit: commit, acquiredAt,
    acquisition: "Public git checkout of the tagged release; no private code used" },
  previousBaseline: { tag: evidence.source.tag, commit: evidence.source.commit, lock: evidencePath,
    note: "Historical fixtures and captures remain pinned to this baseline and are validated against the preserved lock." },
  ...(args.evidence ? { previousTarget: { tag: previous.source.tag, commit: previous.source.commit, tree: previous.source.tree,
    lock: args["previous-path"], note: "The previous target lock, preserved byte-for-byte; inventories generated for that target stay validated against it." } } : {}),
  packageVersions: { root: version("package.json"), codingAgent: version("packages/coding-agent/package.json"), ai: version("packages/ai/package.json"),
    ...Object.fromEntries([["agent", "agent"], ["tui", "tui"], ["codemode", "codemode"], ["mcp", "mcp"]]
      .filter(([, dir]) => args.evidence && exists(`packages/${dir}/package.json`)).map(([key, dir]) => [key, version(`packages/${dir}/package.json`)])) },
  hashPolicy: previous.hashPolicy,
  referenceRuntime: previous.referenceRuntime,
  artifacts: [
    { ...previousArchive, origin: "git archive of acquired public source checkout", acquiredAt,
      command: `git archive --format=tar ${commit}`, sha256: sha256(archive), bytes: archive.length },
    ...rows
  ],
  gaps: previous.gaps
};

writeFileSync(resolve(args.out), `${JSON.stringify(lock, null, 2)}\n`);
console.log(JSON.stringify({ commit, tree, sourceFiles: rows.length,
  counts: Object.fromEntries(Object.entries(summary).map(([key, value]) => [key, value.length])),
  changed: summary.changed, added: summary.added, removed: summary.removed }, null, 2));
