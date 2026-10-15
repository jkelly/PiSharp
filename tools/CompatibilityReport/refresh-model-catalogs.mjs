#!/usr/bin/env node
// Refreshes the CLI's embedded provider catalog shards from a published @earendil-works/pi-ai tarball.
//
// The shards (src/PiSharp.Cli/Models/*.json) are byte-for-byte copies of the package's
// dist/providers/data/*.json. The tool reads the tarball itself (no npm, no extraction), checks that
// every shard matches the SHA256 its providers/data/.manifest.json records and that the manifest lists
// exactly those shards, then:
//   - copies the shards (removing embedded shards the package no longer has);
//   - rewrites the pins in src/PiSharp.Cli/Models/BuiltinModelCatalog.cs (package version, generatedAt,
//     manifest and package SHA256, ShardHashes);
//   - rewrites the copied manifest, its hashes, generatedAt and the shard and model counts in
//     tests/PiSharp.Models.Tests/CatalogCases.cs;
//   - rewrites the catalog paragraph of THIRD-PARTY-NOTICES.md.
// It never regenerates catalog data; it only copies what the package publishes.
//
// Usage: node refresh-model-catalogs.mjs --tarball <earendil-works-pi-ai-X.Y.Z.tgz> [--check]
// Fetch the tarball into a scratch folder, for example: npm pack @earendil-works/pi-ai@1.2.0
// --check reports what would change and exits 1 when anything differs.
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gunzipSync } from "node:zlib";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const args = {};
for (let index = 2; index < process.argv.length; index++) {
  const name = process.argv[index];
  if (name === "--check") args.check = true; else if (name.startsWith("--")) args[name.slice(2)] = process.argv[++index];
}
if (!args.tarball) throw new Error("--tarball <pi-ai package tarball> is required");

const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");
const tarball = readFileSync(resolve(args.tarball));
const tarballSha256 = sha256(tarball);
const integrity = "sha512-" + createHash("sha512").update(tarball).digest("base64");

// Minimal ustar reader (npm tarballs): 512-byte headers, octal sizes, optional pax path records.
function* entries(tar) {
  let offset = 0, paxPath;
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every(byte => byte === 0)) return;
    const field = (start, length) => header.subarray(start, start + length).toString("utf8").replace(/\0.*$/s, "");
    const size = parseInt(field(124, 12).trim() || "0", 8);
    const type = field(156, 1);
    const prefix = field(345, 155);
    const name = paxPath ?? (prefix ? `${prefix}/${field(0, 100)}` : field(0, 100));
    const data = tar.subarray(offset + 512, offset + 512 + size);
    offset += 512 + Math.ceil(size / 512) * 512;
    paxPath = undefined;
    if (type === "x") { paxPath = /\d+ path=([^\n]*)\n/.exec(data.toString("utf8"))?.[1]; continue; }
    if (type === "0" || type === "") yield { name, data };
  }
}
const files = new Map([...entries(gunzipSync(tarball))].map(entry => [entry.name, entry.data]));
const version = JSON.parse(files.get("package/package.json").toString("utf8")).version;
const name = JSON.parse(files.get("package/package.json").toString("utf8")).name;
if (name !== "@earendil-works/pi-ai") throw new Error(`Not the pi-ai package: ${name}`);
const DATA = "package/dist/providers/data/";
const manifestBytes = files.get(DATA + ".manifest.json");
if (!manifestBytes) throw new Error("The package has no providers/data/.manifest.json");
const manifestText = manifestBytes.toString("utf8");
const manifest = JSON.parse(manifestText);
const manifestFiles = Object.entries(manifest.files);
const shardNames = [...files.keys()].filter(path => path.startsWith(DATA) && path.endsWith(".json") && path !== DATA + ".manifest.json").map(path => path.slice(DATA.length));
if (shardNames.length !== manifestFiles.length || !manifestFiles.every(([file]) => shardNames.includes(file)))
  throw new Error("The manifest does not list exactly the package's shards");
for (const [file, hash] of manifestFiles)
  if (sha256(files.get(DATA + file)) !== hash) throw new Error(`${file} does not match its manifest hash`);

const countModels = bytes => Object.values(JSON.parse(bytes.toString("utf8")))
  .reduce((sum, models) => sum + (Array.isArray(models) ? models.length : Object.keys(models).length), 0);
const modelCount = manifestFiles.reduce((sum, [file]) => sum + countModels(files.get(DATA + file)), 0);
const generatedAt = manifest.generatedAt;
const generatedAtMs = Date.parse(generatedAt);
const providers = manifestFiles.map(([file, hash]) => [file.slice(0, -".json".length), hash]);

const changes = [];
const write = (path, next) => {
  const full = resolve(repo, path);
  const current = existsSync(full) ? readFileSync(full) : undefined;
  const bytes = Buffer.isBuffer(next) ? next : Buffer.from(next, "utf8");
  if (current && current.equals(bytes)) return;
  changes.push(path);
  if (!args.check) writeFileSync(full, bytes);
};
const replace = (text, pattern, value, path) => {
  if (!pattern.test(text)) throw new Error(`${path}: pattern not found ${pattern}`);
  return text.replace(pattern, value);
};

// Shards.
const modelsDir = "src/PiSharp.Cli/Models";
for (const [file] of manifestFiles) write(`${modelsDir}/${file}`, files.get(DATA + file));
const known = new Set(manifestFiles.map(([file]) => file));
for (const file of readdirSync(resolve(repo, modelsDir)).filter(file => file.endsWith(".json") && !known.has(file))) {
  changes.push(`${modelsDir}/${file} (removed)`);
  if (!args.check) unlinkSync(resolve(repo, modelsDir, file));
}

// BuiltinModelCatalog.cs.
const catalogPath = `${modelsDir}/BuiltinModelCatalog.cs`;
let catalog = readFileSync(resolve(repo, catalogPath), "utf8");
const grouped = String(generatedAtMs).replace(/\B(?=(\d{3})+(?!\d))/g, "_");
catalog = replace(catalog, /@earendil-works\/pi-ai@\d+\.\d+\.\d+/,`@earendil-works/pi-ai@${version}`, catalogPath);
catalog = replace(catalog, /<c>generatedAt<\/c> \([^)]*\) as Unix milliseconds/, `<c>generatedAt</c> (${generatedAt}) as Unix milliseconds`, catalogPath);
catalog = replace(catalog, /GeneratedAtUnixMilliseconds = [0-9_]+;/, `GeneratedAtUnixMilliseconds = ${grouped};`, catalogPath);
catalog = replace(catalog, /ManifestSha256 = "[0-9a-f]{64}"/, `ManifestSha256 = "${sha256(manifestBytes)}"`, catalogPath);
catalog = replace(catalog, /PackageSha256 = "[0-9a-f]{64}"/, `PackageSha256 = "${tarballSha256}"`, catalogPath);
catalog = replace(catalog, /Every shard of pi-ai \d+\.\d+\.\d+ with/, `Every shard of pi-ai ${version} with`, catalogPath);
catalog = replace(catalog, /(ShardHashes = new Dictionary<string, string>\(StringComparer\.Ordinal\)\r?\n\s*\{\r?\n)[\s\S]*?(\r?\n\s*\}\.ToImmutableDictionary)/,
  (_, open, close) => open + providers.map(([provider, hash]) => `        ["${provider}"] = "${hash}",`).join("\n") + close, catalogPath);
write(catalogPath, catalog);

// CatalogCases.cs.
const testsPath = "tests/PiSharp.Models.Tests/CatalogCases.cs";
let tests = readFileSync(resolve(repo, testsPath), "utf8");
if (manifestText.endsWith("\n") && manifestText.slice(0, -1).includes("\n")) throw new Error("Multi-line manifest: the test copy expects one line");
const manifestLine = manifestText.replace(/\n$/, "");
if (manifestLine + "\n" !== manifestText) throw new Error("Unexpected manifest framing: the test hashes the copy plus a newline");
tests = replace(tests, /(providers\/data\/\.manifest\.json of @earendil-works\/pi-ai@)\d+\.\d+\.\d+/,`$1${version}`, testsPath);
tests = replace(tests, /private const string Manifest = """.*""";/, () => `private const string Manifest = """${manifestLine}""";`, testsPath);
tests = replace(tests, /private const string ManifestSha256 = "[0-9a-f]{64}";/, `private const string ManifestSha256 = "${sha256(manifestBytes)}";`, testsPath);
tests = replace(tests, /private const string TarballSha256 = "[0-9a-f]{64}";/, `private const string TarballSha256 = "${tarballSha256}";`, testsPath);
tests = replace(tests, /Equal\(\d+, files\.Count, "manifest shards"\)/, `Equal(${providers.length}, files.Count, "manifest shards")`, testsPath);
tests = replace(tests, /Equal\(\d+, models, "catalog entries across all shards"\)/, `Equal(${modelCount}, models, "catalog entries across all shards")`, testsPath);
tests = replace(tests, /Equal\(\d+, seen\.Count, "tarball shards compared"\)/, `Equal(${providers.length}, seen.Count, "tarball shards compared")`, testsPath);
tests = replace(tests, /DateTimeOffset\.Parse\("[^"]+", System\.Globalization/, `DateTimeOffset.Parse("${generatedAt}", System.Globalization`, testsPath);
write(testsPath, tests);

// THIRD-PARTY-NOTICES.md.
const noticesPath = "THIRD-PARTY-NOTICES.md";
let notices = readFileSync(resolve(repo, noticesPath), "utf8");
const ids = providers.map(([provider]) => provider).sort();
notices = replace(notices, /\(all \d+ provider catalog shards, ([\w-]+) through ([\w-]+)\)/, `(all ${providers.length} provider catalog shards, ${ids[0]} through ${ids.at(-1)})`, noticesPath);
notices = replace(notices, /from the Pi AI \d+\.\d+\.\d+ package `@earendil-works\/pi-ai@\d+\.\d+\.\d+` \(SHA256 [0-9a-f]{64}, npm integrity [A-Za-z0-9+/=-]+\)/,
  `from the Pi AI ${version} package \`@earendil-works/pi-ai@${version}\` (SHA256 ${tarballSha256}, npm integrity ${integrity})`, noticesPath);
notices = replace(notices, /`providers\/data\/\.manifest\.json` \(SHA256 [0-9a-f]{64}, generatedAt [^)]+\)/,
  `\`providers/data/.manifest.json\` (SHA256 ${sha256(manifestBytes)}, generatedAt ${generatedAt})`, noticesPath);
write(noticesPath, notices);

console.log(JSON.stringify({ package: `${name}@${version}`, tarballSha256, integrity, manifestSha256: sha256(manifestBytes), generatedAt,
  shards: providers.length, models: modelCount, changed: changes }, null, 2));
if (args.check && changes.length > 0) process.exit(1);
