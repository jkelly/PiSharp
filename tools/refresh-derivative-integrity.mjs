#!/usr/bin/env node
// Refreshes the current-byte columns of PUBLIC-DERIVATIVE-INTEGRITY.json after reviewed edits and
// re-seals its digest in tools/PublicDerivativeIntegrity.mjs. Original (historical) columns and
// historicalExecutionPins are never rewritten. --move old=new re-paths a row whose bytes moved.
// Usage: node tools/refresh-derivative-integrity.mjs [--move old=new ...] [--check]
import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const tablePath = resolve(repo, "PUBLIC-DERIVATIVE-INTEGRITY.json");
const verifierPath = resolve(repo, "tools/PublicDerivativeIntegrity.mjs");
const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");
const args = process.argv.slice(2);
const checkOnly = args.includes("--check");
const moves = new Map(args.flatMap((value, index) => value === "--move" ? [args[index + 1].split("=")] : []));

const original = readFileSync(tablePath, "utf8");
const table = JSON.parse(original);
// The table is stored byte-exact (text attribute unset); keep its line endings and final-newline style.
const newline = original.includes("\r\n") ? "\r\n" : "\n";
const trailing = original.endsWith("\n");
const changes = [];
for (const row of table.files) {
  if (moves.has(row.path)) { changes.push({ path: row.path, movedTo: moves.get(row.path) }); row.path = moves.get(row.path); }
  if (!existsSync(resolve(repo, row.path))) throw new Error(`Pinned derivative file is missing: ${row.path}`);
  const bytes = readFileSync(resolve(repo, row.path));
  if (sha256(bytes) !== row.currentSha256 || bytes.length !== row.currentBytes) {
    changes.push({ path: row.path, from: row.currentSha256, to: sha256(bytes) });
    row.currentSha256 = sha256(bytes);
    row.currentBytes = bytes.length;
  }
}
for (const pin of table.currentDependencies) {
  const bytes = readFileSync(resolve(repo, pin.path));
  if (sha256(bytes) !== pin.sha256 || bytes.length !== pin.bytes) { changes.push({ dependency: pin.path }); pin.sha256 = sha256(bytes); pin.bytes = bytes.length; }
}
if (new Set(table.files.map(row => row.path)).size !== table.files.length) throw new Error("Duplicate derivative paths after moves.");

console.log(JSON.stringify({ rows: table.files.length, changed: changes.length, changes }, null, 2));
if (checkOnly) { process.exitCode = changes.length ? 1 : 0; }
else if (changes.length) {
  const bytes = Buffer.from(JSON.stringify(table, null, 2).replace(/\n/g, newline) + (trailing ? newline : ""));
  writeFileSync(tablePath, bytes);
  const verifier = readFileSync(verifierPath, "utf8");
  const sealed = verifier.replace(/const tableSha256 = '[0-9a-f]{64}';/, `const tableSha256 = '${sha256(bytes)}';`);
  if (sealed === verifier) throw new Error("Could not re-seal tableSha256 in tools/PublicDerivativeIntegrity.mjs.");
  writeFileSync(verifierPath, sealed);
}
