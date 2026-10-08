// Anonymous official artifact reads only; no install, extraction, or asset execution.
import { get } from "node:https";
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { posix, resolve } from "node:path";
import { gunzipSync } from "node:zlib";
import { execFileSync } from "node:child_process";

const root = dirname(fileURLToPath(import.meta.url));
const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");
const version = "0.99.1";
const releaseRoot = "https://github.com/earendil-works/pi/releases/download/v0.99.1/";
const expectedArchiveHash = "4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b";
const expectedSumsHash = "1490fbb52c4cbf6f175b1b66147702b5e6584504277fc0c424745c32770bdbba";
const packageNames = [
  "pi-agent-core", "pi-ai", "chord", "pi-client", "pi-codemode", "pi-coding-agent",
  "pi-durable", "pi-mcp", "pi-protocol", "pi-server", "pi-telemetry", "pi-tui",
  "pi-session-backend-sqlite-node",
].map((name) => `@earendil-works/${name}`);
const outputNames = ["pi-0.99.1-source.tar.gz", "SHA256SUMS", "metadata.json"];
const metadataUrls = [
  "https://api.github.com/repos/earendil-works/pi/releases/tags/v0.99.1",
  ...packageNames.map((name) => `https://registry.npmjs.org/${name.replace("/", "%2f")}/${version}`),
];

function request(url, limit, redirects = 0, chain = []) {
  const parsed = new URL(url);
  const allowedHosts = new Set(["github.com", "api.github.com", "registry.npmjs.org", "release-assets.githubusercontent.com", "objects.githubusercontent.com"]);
  if (parsed.protocol !== "https:" || parsed.username || parsed.password || !allowedHosts.has(parsed.hostname)) throw new Error("Unapproved download destination");
  if (redirects > 5) throw new Error("Too many redirects");
  return new Promise((resolve, reject) => {
    const req = get(parsed, { headers: { "User-Agent": "PiSharp-public-baseline-research", Accept: "application/json, application/octet-stream;q=0.9", "Accept-Encoding": "identity" } }, (res) => {
      if ([301, 302, 303, 307, 308].includes(res.statusCode)) {
        res.resume();
        if (!res.headers.location) return reject(new Error("Redirect lacks Location"));
        const next = new URL(res.headers.location, parsed);
        // Temporary asset signatures are deliberately excluded from recorded redirect URLs.
        const safeLocation = `${next.origin}${next.pathname}`;
        request(next.href, limit, redirects + 1, [...chain, safeLocation]).then(resolve, reject);
        return;
      }
      const chunks = [];
      let bytes = 0;
      res.on("data", (chunk) => {
        bytes += chunk.length;
        if (bytes > limit) req.destroy(new Error("Download size limit exceeded"));
        else chunks.push(chunk);
      });
      res.on("error", reject);
      res.on("end", () => resolve({ bytes: Buffer.concat(chunks), status: res.statusCode, redirects: chain, headers: { "content-type": res.headers["content-type"] ?? null, etag: res.headers.etag ?? null, "last-modified": res.headers["last-modified"] ?? null }, fetchedAt: new Date().toISOString() }));
    });
    req.on("error", reject);
    req.setTimeout(20000, () => req.destroy(new Error("Read timeout")));
  });
}

function parseTar(bytes, prefix) {
  const files = new Map();
  const kinds = {};
  let offset = 0, globalPax = {}, localPax = {}, metadataHeaders = 0;
  const str = (header, start, length) => header.subarray(start, start + length).toString("utf8").replace(/\0.*$/s, "");
  const octal = (header, start, length) => {
    const value = str(header, start, length).trim();
    if (!/^[0-7]*$/.test(value)) throw new Error("Unsupported tar numeric encoding");
    const parsed = value ? parseInt(value, 8) : 0;
    if (!Number.isSafeInteger(parsed)) throw new Error("Unsafe tar size");
    return parsed;
  };
  function safeName(name, allowRoot = false) {
    if (name.includes("\\") || name.includes("\0") || name.startsWith("/") || /^[A-Za-z]:/.test(name)) throw new Error("Unsafe archive path");
    const trimmed = name.replace(/\/$/, "");
    if (trimmed.split("/").some((part) => !part || part === "." || part === "..")) throw new Error("Unsafe archive path component");
    if (prefix && !(name.startsWith(prefix) || (allowRoot && `${trimmed}/` === prefix))) throw new Error("Archive path outside release root");
    return prefix ? name.slice(prefix.length).replace(/\/$/, "") : trimmed;
  }
  function pax(data) {
    const attributes = {};
    let index = 0;
    while (index < data.length) {
      const space = data.indexOf(32, index);
      if (space < 0) throw new Error("Invalid PAX record");
      const lengthText = data.subarray(index, space).toString("ascii");
      if (!/^[1-9][0-9]*$/.test(lengthText)) throw new Error("Invalid PAX length");
      const length = Number(lengthText), end = index + length;
      if (!Number.isSafeInteger(length) || end > data.length || data[end - 1] !== 10) throw new Error("Invalid PAX bounds");
      const record = data.subarray(space + 1, end - 1).toString("utf8");
      const eq = record.indexOf("=");
      if (eq < 1) throw new Error("Invalid PAX attribute");
      const key = record.slice(0, eq);
      if (key.startsWith("GNU.sparse") || key === "size") throw new Error("Unsupported PAX sparse/size override");
      if (Object.hasOwn(attributes, key)) throw new Error("Duplicate PAX attribute");
      attributes[key] = record.slice(eq + 1);
      index = end;
    }
    return attributes;
  }
  while (offset + 512 <= bytes.length) {
    const header = bytes.subarray(offset, offset + 512);
    if (header.every((byte) => byte === 0)) {
      if (!bytes.subarray(offset).every((byte) => byte === 0)) throw new Error("Nonzero bytes after tar end");
      if (Object.keys(localPax).length) throw new Error("Dangling PAX attributes");
      return { files, kinds, metadataHeaders };
    }
    const checksum = [...header].reduce((sum, byte, index) => sum + (index >= 148 && index < 156 ? 32 : byte), 0);
    if (checksum !== octal(header, 148, 8)) throw new Error("Tar header checksum mismatch");
    const size = octal(header, 124, 12);
    const start = offset + 512, end = start + size;
    if (end > bytes.length) throw new Error("Truncated tar member");
    const data = bytes.subarray(start, end);
    const type = str(header, 156, 1) || "0";
    offset = start + Math.ceil(size / 512) * 512;
    if (type === "x" || type === "g") {
      const values = pax(data);
      if (type === "g" && (values.path || values.linkpath)) throw new Error("Global PAX path override rejected");
      if (type === "g") globalPax = { ...globalPax, ...values };
      else localPax = values;
      metadataHeaders++;
      continue;
    }
    const attributes = { ...globalPax, ...localPax };
    localPax = {};
    const name = attributes.path ?? [str(header, 345, 155), str(header, 0, 100)].filter(Boolean).join("/");
    const member = safeName(name, type === "5");
    if (member && files.has(member)) throw new Error(`Duplicate archive path: ${member}`);
    if (!["0", "5", "1", "2"].includes(type)) throw new Error(`Unsupported tar member type ${type}`);
    let link = null;
    if (type === "1" || type === "2") {
      link = attributes.linkpath ?? str(header, 157, 100);
      if (link.includes("\\") || link.startsWith("/") || /^[A-Za-z]:/.test(link) || link.includes("\0")) throw new Error("Unsafe archive link");
      const target = type === "1" ? link : posix.normalize(posix.join(posix.dirname(name), link));
      safeName(target);
      if (size !== 0) throw new Error("Archive link unexpectedly has content");
    }
    if (type === "5" && size !== 0) throw new Error("Directory unexpectedly has content");
    kinds[type] = (kinds[type] ?? 0) + 1;
    if (member) files.set(member, { type, data, link });
  }
  throw new Error("Tar has no complete end marker");
}

const sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
function inspect(sourcePath) {
  const source = resolve(sourcePath);
  const git = (...args) => execFileSync("git", ["-c", `safe.directory=${source.replaceAll("\\", "/")}`, "-C", source, ...args], { maxBuffer: 256 * 1024 * 1024 });
  if (git("rev-parse", "HEAD").toString().trim() !== sourceSha || git("rev-parse", "v0.99.1^{commit}").toString().trim() !== sourceSha) throw new Error("Pinned source/tag mismatch");
  if (git("status", "--porcelain", "--untracked-files=all").length) throw new Error("Source tree is not clean");
  const archiveBytes = readFileSync(join(root, outputNames[0]));
  const sumBytes = readFileSync(join(root, outputNames[1]));
  if (sha256(archiveBytes) !== expectedArchiveHash || sha256(sumBytes) !== expectedSumsHash) throw new Error("Official artifact hash mismatch");
  if (!sumBytes.toString("utf8").split(/\r?\n/).includes(`${expectedArchiveHash}  ${outputNames[0]}`)) throw new Error("Official source checksum entry mismatch");
  const archive = parseTar(gunzipSync(archiveBytes, { maxOutputLength: 256 * 1024 * 1024 }), "pi-0.99.1/");
  const canonical = parseTar(git("archive", "--format=tar", sourceSha), null);
  let comparedFiles = 0, comparedLinks = 0;
  for (const [path, file] of canonical.files) {
    if (file.type === "5") continue;
    const acquired = archive.files.get(path);
    if (!acquired || file.type !== acquired.type || file.link !== acquired.link || !file.data.equals(acquired.data)) throw new Error(`Source archive differs from pinned Git bytes: ${path}`);
    if (file.type === "0") comparedFiles++;
    else comparedLinks++;
  }
  const additions = [...archive.files].filter(([path, file]) => file.type !== "5" && !canonical.files.has(path));
  const dataPrefix = "packages/ai/src/providers/data/";
  if (!additions.length || additions.some(([path, file]) => file.type !== "0" || !path.startsWith(dataPrefix) || path.slice(dataPrefix.length).includes("/") || !path.endsWith(".json"))) throw new Error("Unexpected source archive additions");
  const manifestBytes = archive.files.get(`${dataPrefix}.manifest.json`)?.data;
  if (!manifestBytes) throw new Error("Released model manifest absent");
  const manifest = JSON.parse(manifestBytes.toString("utf8"));
  if (manifest.schemaVersion !== 6 || typeof manifest.generatedAt !== "string" || !Number.isFinite(Date.parse(manifest.generatedAt))) throw new Error("Released model manifest schema/time mismatch");
  const aggregator = archive.files.get("packages/ai/src/models.generated.ts").data.toString("utf8");
  const providerIds = [...aggregator.matchAll(/^import \{ [A-Z][A-Z0-9_]*_CLASSIFIER_MODELS, [A-Z][A-Z0-9_]*_IMAGE_MODELS, [A-Z][A-Z0-9_]*_MODELS \} from "\.\/providers\/([^"/]+)\.models\.ts";$/gm)].map((match) => match[1]).sort();
  if (!providerIds.length || new Set(providerIds).size !== providerIds.length) throw new Error("Released provider import set mismatch");
  const expectedNames = providerIds.map((provider) => `${provider}.json`).sort();
  if (JSON.stringify(Object.keys(manifest.files).sort()) !== JSON.stringify(expectedNames) || additions.length !== expectedNames.length + 1) throw new Error("Released provider manifest/file set mismatch");
  const structure = {}, catalogFiles = [], modelTypes = {}, apis = {};
  let modelCount = 0;
  for (const provider of providerIds) {
    const path = `${dataPrefix}${provider}.json`, bytes = archive.files.get(path)?.data;
    if (!bytes || sha256(bytes) !== manifest.files[`${provider}.json`]) throw new Error(`Released catalog file checksum mismatch: ${provider}`);
    const groups = JSON.parse(bytes.toString("utf8")), models = new Map();
    for (const [api, values] of Object.entries(groups)) {
      if (!values || typeof values !== "object" || Array.isArray(values)) throw new Error("Catalog API group must be an object");
      for (const [key, model] of Object.entries(values)) {
        if (models.has(key) || key !== `${model.type}:${model.id}` || model.provider !== provider || model.api !== api) throw new Error("Catalog model identity/group mismatch");
        models.set(key, api);
        modelTypes[model.type] = (modelTypes[model.type] ?? 0) + 1;
        apis[api] = (apis[api] ?? 0) + 1;
      }
    }
    structure[provider] = Object.fromEntries([...models].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0));
    catalogFiles.push({ path, bytes: bytes.length, sha256: sha256(bytes), models: models.size });
    modelCount += models.size;
  }
  const structureHash = sha256(JSON.stringify(structure));
  if (manifest.structureHash !== structureHash) throw new Error("Released catalog structure hash mismatch");
  const metadata = JSON.parse(readFileSync(join(root, "metadata.json"), "utf8"));
  for (const entry of metadata.metadata) if (entry.bytes !== Buffer.byteLength(entry.rawBody) || sha256(entry.rawBody) !== entry.sha256) throw new Error("Raw metadata byte hash mismatch");
  const release = JSON.parse(metadata.metadata.find((entry) => entry.url.startsWith("https://api.github.com/")).rawBody);
  if (release.tag_name !== "v0.99.1" || release.draft || release.prerelease || release.html_url !== "https://github.com/earendil-works/pi/releases/tag/v0.99.1") throw new Error("Official release identity mismatch");
  const asset = release.assets.find((entry) => entry.name === outputNames[0]);
  if (asset?.digest !== `sha256:${expectedArchiveHash}` || asset.size !== archiveBytes.length || asset.browser_download_url !== `${releaseRoot}${outputNames[0]}`) throw new Error("Official API archive digest/size/URL mismatch");
  const packages = metadata.metadata.filter((entry) => entry.url.startsWith("https://registry.npmjs.org/")).map((entry) => {
    if (entry.status !== 200) throw new Error("Public package version missing");
    const pkg = JSON.parse(entry.rawBody);
    if (!packageNames.includes(pkg.name) || pkg.version !== version || pkg.gitHead !== sourceSha || pkg.license !== "MIT") throw new Error("Public package metadata identity mismatch");
    const expectedUrl = `https://registry.npmjs.org/${pkg.name}/-/${pkg.name.split("/")[1]}-${version}.tgz`;
    if (pkg.dist.tarball !== expectedUrl || !/^sha512-[A-Za-z0-9+/]+=*$/.test(pkg.dist.integrity)) throw new Error("Public package tarball/integrity metadata mismatch");
    return { name: pkg.name, version: pkg.version, licenseDeclaration: pkg.license, gitHead: pkg.gitHead, metadataUrl: entry.url, metadataSha256: entry.sha256, tarballUrl: pkg.dist.tarball, integrity: pkg.dist.integrity, shasum: pkg.dist.shasum ?? null, reportedFiles: pkg.dist.fileCount ?? null, reportedUnpackedBytes: pkg.dist.unpackedSize ?? null, registrySignatures: pkg.dist.signatures ?? [], registryAttestations: pkg.dist.attestations ?? null, archiveAcquired: false, signatureVerified: false, attestationVerified: false };
  });
  const license = archive.files.get("LICENSE").data;
  if (!license.equals(canonical.files.get("LICENSE").data)) throw new Error("Source license differs from pinned source");
  const nativeArtifacts = [...archive.files].filter(([path, file]) => file.type === "0" && /\.(node|wasm|dll|so|dylib)$/.test(path)).map(([path, file]) => ({ path, bytes: file.data.length, sha256: sha256(file.data) }));
  return { schemaVersion: 1, kind: "official-released-source-and-registry-metadata-inspection", sourceSha, release: { tag: release.tag_name, publishedAt: release.published_at, htmlUrl: release.html_url, apiReportedImmutable: release.immutable ?? null, archiveUrl: asset.browser_download_url, archiveBytes: archiveBytes.length, archiveSha256: expectedArchiveHash, checksumFileSha256: expectedSumsHash }, archiveSafety: { extracted: false, executed: false, memberKinds: archive.kinds, paxMetadataHeaders: archive.metadataHeaders, confinedReleaseRoot: "pi-0.99.1/", absoluteTraversalAndEscapingLinksRejected: true }, sourceComparison: { authority: "canonical git archive bytes at exact pinned commit", comparedRegularFiles: comparedFiles, comparedLinks, differingTrackedFiles: 0, addedRegularFiles: additions.map(([path, file]) => ({ path, bytes: file.data.length, sha256: sha256(file.data) })) }, sourceLicense: { path: "LICENSE", bytes: license.length, sha256: sha256(license), declaration: "MIT", attribution: "Copyright (c) 2025 Mario Zechner", fullDependencyAndCatalogLicensingReviewed: false }, catalog: { scope: "publisher source archive snapshot; npm tarball equality unverified", manifestPath: `${dataPrefix}.manifest.json`, manifestBytes: manifestBytes.length, manifestSha256: sha256(manifestBytes), schemaVersion: manifest.schemaVersion, generatedAt: manifest.generatedAt, structureHash, providers: providerIds.length, models: modelCount, modelTypes, apis, files: catalogFiles, allFileAndStructureHashesMatched: true, rawNumericLexemesPreservedInArchive: true, reconstructed: false }, publicNpmVersionMetadata: packages, privateWorkspaceExcluded: "@earendil-works/pi-evals", nativeAssetsNotExecuted: nativeArtifacts, limitations: ["Published npm tarball bytes, packaged LICENSE contents, signatures and provenance attestations remain unverified.", "Npm and GitHub release builds hydrate generated catalog data separately; equality cannot be inferred from source commit or version.", "Archive acquisition does not acquire/install runtime dependency closure or validate provider runtime behavior.", "Source MIT identity is verified; complete transitive/native/data-source licensing review remains open.", "Live pi.dev catalog is an independently mutable overlay and is not used to reconstruct this released snapshot.", "Original baseline.lock and developer oracle locks are immutable; this evidence does not close full phase gates."] };
}

const args = process.argv.slice(2);
if (args[0] === "--inspect-new" || args[0] === "--verify") {
  if (args.length !== 3 || args[1] !== "--source") throw new Error("Usage: node acquire.mjs --inspect-new|--verify --source PINNED_UPSTREAM_PATH");
  const reportName = "inspection.json", lockName = "evidence.lock.json";
  const frozenNames = ["acquire.mjs", ...outputNames, reportName];
  if (args[0] === "--verify") {
    const lock = JSON.parse(readFileSync(join(root, lockName), "utf8"));
    if (lock.sourceSha !== sourceSha || lock.node.version !== process.version || lock.node.sha256 !== sha256(readFileSync(process.execPath))) throw new Error("Evidence runtime/source lock mismatch");
    for (const file of lock.files) if (sha256(readFileSync(join(root, file.path))) !== file.sha256) throw new Error(`Immutable evidence changed: ${file.path}`);
    const actual = JSON.stringify(inspect(args[2]), null, 2) + "\n";
    if (actual !== readFileSync(join(root, reportName), "utf8")) throw new Error("Offline inspection differs from frozen report");
    const observed = JSON.parse(actual);
    console.log(JSON.stringify({ verified: true, sourceSha, immutableFiles: lock.files.length, officialSourceArchive: expectedArchiveHash, catalogProviders: observed.catalog.providers, catalogModels: observed.catalog.models, publishedPackageDocuments: observed.publicNpmVersionMetadata.length }));
  } else {
    if (existsSync(join(root, reportName)) || existsSync(join(root, lockName))) throw new Error("Refusing to overwrite frozen inspection/lock");
    const report = inspect(args[2]);
    writeFileSync(join(root, reportName), JSON.stringify(report, null, 2) + "\n", { flag: "wx" });
    const files = frozenNames.map((path) => { const bytes = readFileSync(join(root, path)); return { path, bytes: bytes.length, sha256: sha256(bytes) }; });
    writeFileSync(join(root, lockName), JSON.stringify({ schemaVersion: 1, sourceSha, createdAt: new Date().toISOString(), node: { version: process.version, sha256: sha256(readFileSync(process.execPath)) }, files, mode: "default verify is offline and immutable; first acquisition/inspection refuse overwrite", artifactAuthority: "official GitHub release source archive and raw official npm version metadata; no install/extraction/asset execution" }, null, 2) + "\n", { flag: "wx" });
    console.log(JSON.stringify({ frozen: true, sourceSha, trackedFilesMatched: report.sourceComparison.comparedRegularFiles, catalogProviders: report.catalog.providers, catalogModels: report.catalog.models, publishedPackageDocuments: report.publicNpmVersionMetadata.length }));
  }
  process.exit(0);
}
if (args.join(" ") !== "--acquire-new") throw new Error("Usage: node acquire.mjs --verify --source PINNED_UPSTREAM_PATH (or first-new acquisition/inspection)");
for (const name of outputNames) if (existsSync(join(root, name))) throw new Error(`Refusing to overwrite ${name}`);
const archive = await request(`${releaseRoot}pi-0.99.1-source.tar.gz`, 20 * 1024 * 1024);
if (archive.status !== 200 || sha256(archive.bytes) !== expectedArchiveHash) throw new Error("Official source archive status/hash mismatch");
const sums = await request(`${releaseRoot}SHA256SUMS`, 65536);
if (sums.status !== 200 || sha256(sums.bytes) !== expectedSumsHash) throw new Error("Official SHA256SUMS status/hash mismatch");
const sumLine = sums.bytes.toString("utf8").split(/\r?\n/).find((line) => line.endsWith("  pi-0.99.1-source.tar.gz"));
if (sumLine !== `${expectedArchiveHash}  pi-0.99.1-source.tar.gz`) throw new Error("Source archive absent/mismatched in official checksum file");
const metadata = [];
for (const url of metadataUrls) {
  const result = await request(url, 4 * 1024 * 1024);
  const body = result.bytes.toString("utf8");
  if (![200, 404].includes(result.status)) throw new Error(`Metadata status ${result.status}: ${url}`);
  JSON.parse(body); // Retain exact UTF-8 bodies; parsed values are not the archival evidence.
  metadata.push({ url, fetchedAt: result.fetchedAt, status: result.status, headers: result.headers, redirects: result.redirects, bytes: result.bytes.length, sha256: sha256(result.bytes), rawBody: body });
}
writeFileSync(join(root, outputNames[0]), archive.bytes, { flag: "wx" });
writeFileSync(join(root, outputNames[1]), sums.bytes, { flag: "wx" });
writeFileSync(join(root, outputNames[2]), JSON.stringify({ schemaVersion: 1, mode: "anonymous-read-only", releaseTag: "v0.99.1", acquisitions: [{ url: `${releaseRoot}${outputNames[0]}`, fetchedAt: archive.fetchedAt, status: archive.status, redirects: archive.redirects, headers: archive.headers, bytes: archive.bytes.length, sha256: sha256(archive.bytes) }, { url: `${releaseRoot}${outputNames[1]}`, fetchedAt: sums.fetchedAt, status: sums.status, redirects: sums.redirects, headers: sums.headers, bytes: sums.bytes.length, sha256: sha256(sums.bytes) }], metadata }, null, 2) + "\n", { flag: "wx" });
console.log(JSON.stringify({ acquired: outputNames, archiveBytes: archive.bytes.length, archiveSha256: sha256(archive.bytes), metadataResponses: metadata.length, publishedVersionDocuments: metadata.filter((entry) => entry.status === 200 && entry.url.startsWith("https://registry.npmjs.org/")).length }));
