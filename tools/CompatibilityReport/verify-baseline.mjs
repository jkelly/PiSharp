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
check(text("rev-parse", "HEAD") === sha && text("status", "--porcelain", "--untracked-files=all") === "", "Unmodified upstream checkout at pinned SHA");
check(text("rev-parse", `${sha}^{tree}`) === target.source.tree && text("rev-parse", `${target.source.tag}^{commit}`) === sha, "Tree and tag resolve to locked source");

const rows = target.artifacts.filter(row => row.kind === "source-file");
const paths = new Set(rows.map(row => row.path));
check(paths.size === rows.length, "Unique target source files");
for (const row of rows) {
  const blob = git("show", `${sha}:${row.path}`);
  const checkout = readFileSync(resolve(upstream, row.path));
  check(sha256(blob) === row.sha256 && blob.length === row.bytes && sha256(checkout) === row.checkoutSha256 && checkout.length === row.checkoutBytes,
    `Canonical blob and checkout bytes ${row.path}`);
}
const archive = git("archive", "--format=tar", sha);
const lockedArchive = target.artifacts.find(row => row.kind === "source-archive");
check(sha256(archive) === lockedArchive.sha256 && archive.length === lockedArchive.bytes, "Reproduced canonical source tar SHA256");

for (const name of ["surfaces", "parity"]) check(read(`compatibility/${name}.json`).sourceSha === sha, `${name}.json names the target baseline`);
for (const row of read("compatibility/surfaces.json").requirements)
  check(row.source.sha === sha && row.source.url === `https://github.com/earendil-works/pi/blob/${sha}/${row.source.path}` && paths.has(row.source.path), `Pinned source link ${row.id}`);
const release = read("compatibility/public-release.json");
check(release.upstream.tag === target.source.tag && release.upstream.commit === sha, "Release record names the target baseline");

console.log(JSON.stringify({ sourceSha: sha, tag: target.source.tag, evidenceSourceSha: evidence.source.commit,
  verifiedSourceFiles: rows.length, sourceArchiveVerified: true, checksPassed: checks.length }, null, 2));
