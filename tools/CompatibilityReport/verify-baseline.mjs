#!/usr/bin/env node
// Verifies compatibility/target.lock.json against a clean upstream checkout, plus the inventory links
// and the preserved evidence lock. This is the same --upstream logic check.mjs applies, runnable
// without the fixture corpus or the pinned development Node executable.
// Usage: node verify-baseline.mjs --upstream <checkout at the locked tag>
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { resolveSourceLock } from "./source-lock.mjs";
import { readSourceFiles } from "./git-source.mjs";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const index = process.argv.indexOf("--upstream");
if (index < 0) throw new Error("--upstream <checkout> is required");
const upstream = resolve(process.argv[index + 1]);
const read = path => JSON.parse(readFileSync(resolve(repo, path), "utf8"));
const git = (...command) => execFileSync("git", ["-C", upstream, ...command], { maxBuffer: 1 << 30 });
const text = (...command) => git(...command).toString().trim();
const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");
const checks = [];
const check = (condition, name) => { checks.push(name); if (!condition) throw new Error(`Failed: ${name}`); };

const target = read("compatibility/target.lock.json");
const evidence = read(target.previousBaseline.lock);
const sha = target.source.commit;
check(target.previousBaseline.commit === evidence.source.commit && target.previousBaseline.tag === evidence.source.tag, "Target lock names the preserved evidence lock");
if (target.previousTarget) {
  const previousTarget = read(target.previousTarget.lock);
  check(previousTarget.source.commit === target.previousTarget.commit && previousTarget.source.tag === target.previousTarget.tag && previousTarget.source.tree === target.previousTarget.tree &&
    previousTarget.previousBaseline.commit === evidence.source.commit, "Target lock names the preserved previous target lock");
}
check(text("rev-parse", "HEAD") === sha && text("status", "--porcelain", "--untracked-files=all") === "", "Unmodified upstream checkout at pinned SHA");
check(text("rev-parse", `${sha}^{tree}`) === target.source.tree && text("rev-parse", `${target.source.tag}^{commit}`) === sha, "Tree and tag resolve to locked source");

const rows = target.artifacts.filter(row => row.kind === "source-file");
const paths = new Set(rows.map(row => row.path));
check(paths.size === rows.length, "Unique target source files");
const sourceFiles = readSourceFiles(upstream, sha, [...paths]);
for (const row of rows) {
  const sourceFile = sourceFiles.get(row.path);
  check(sourceFile?.gitBlob === row.gitBlob, `Canonical Git blob ${row.path}`);
  const blob = sourceFile.bytes;
  const checkout = readFileSync(resolve(upstream, row.path));
  check(sha256(blob) === row.sha256 && blob.length === row.bytes && sha256(checkout) === row.checkoutSha256 && checkout.length === row.checkoutBytes,
    `Canonical blob and checkout bytes ${row.path}`);
}
const archive = git("archive", "--format=tar", sha);
const lockedArchive = target.artifacts.find(row => row.kind === "source-archive");
check(sha256(archive) === lockedArchive.sha256 && archive.length === lockedArchive.bytes, "Reproduced canonical source tar SHA256");

const inventory = read("compatibility/surfaces.json");
const inventoryLock = resolveSourceLock(read, target, inventory.sourceSha);
const inventorySha = inventoryLock.source.commit;
const inventoryPaths = new Set(inventoryLock.artifacts.filter(row => row.kind === "source-file").map(row => row.path));
for (const name of ["surfaces", "parity"]) check(read(`compatibility/${name}.json`).sourceSha === inventorySha, `${name}.json names its preserved source baseline`);
for (const row of read("compatibility/surfaces.json").requirements)
  check(row.source.sha === inventorySha && row.source.url === `https://github.com/earendil-works/pi/blob/${inventorySha}/${row.source.path}` && inventoryPaths.has(row.source.path), `Pinned source link ${row.id}`);
const release = read("compatibility/public-release.json");
const releaseLock = resolveSourceLock(read, target, release.upstream.commit);
check(release.upstream.tag === releaseLock.source.tag, "Release record names its preserved source baseline");

console.log(JSON.stringify({ sourceSha: sha, tag: target.source.tag, evidenceSourceSha: evidence.source.commit,
  inventorySourceSha: inventorySha, publishedReleaseSourceSha: releaseLock.source.commit,
  verifiedSourceFiles: rows.length, sourceArchiveVerified: true, checksPassed: checks.length }, null, 2));
