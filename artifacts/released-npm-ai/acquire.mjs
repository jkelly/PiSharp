// Inspect the official pi-ai tarball as inert bytes; never install/extract/execute it.
import { get } from "node:https";
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { dirname, join, resolve, posix } from "node:path";
import { fileURLToPath } from "node:url";
import { gunzipSync } from "node:zlib";

const root = dirname(fileURLToPath(import.meta.url));
const hash = (bytes, algorithm = "sha256", encoding = "hex") => createHash(algorithm).update(bytes).digest(encoding);
const sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
const nodeHash = "3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237";
const tarballUrl = "https://registry.npmjs.org/@earendil-works/pi-ai/-/pi-ai-0.99.1.tgz";
const integrity = "sha512-4nV9JKc94iPX8bwdGPc2nTuVPKIPsffhnp3WoN9NYCNqbtoOF8LhYcIs/+Sn/alroqJK/5QRu6/Z6Ck+n0hyBA==";
const shasum = "2945bf014fbb314bd37b919e83317560e0a8fa1d";
const baselinePins = [
  { path: "metadata.json", sha256: "1aafbfc3e2a0610b4066dd4cd94e239a64bc3b08506cd0d67f41cd18a34af789" },
  { path: "inspection.json", sha256: "c99a5a5db625fc4ee8dbcb2b69b6e803bd3c88943105d6c4a64ae6df757c490b" },
  { path: "evidence.lock.json", sha256: "6fe01c9a77d39505c154b1b20c65d838513a8efba17ea58164aaa38e7b81456c" },
  { path: "pi-0.99.1-source.tar.gz", sha256: "4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b" },
];
const frozenNames = ["acquire.mjs", "pi-ai-0.99.1.tgz", "acquisition.json", "inspection.json"];
const args = process.argv.slice(2);
const mode = args[0] ?? "--verify";
if (!["--verify", "--acquire-new", "--inspect-new"].includes(mode) || (args.length !== 0 && args.length !== 1 && !(args.length === 3 && args[1] === "--baseline"))) throw new Error("Usage: node acquire.mjs [--verify|--acquire-new|--inspect-new] [--baseline FROZEN_RELEASE_EVIDENCE_DIRECTORY]");
const baseline = resolve(args[2] ?? join(root, "..", "released-baseline"));
if (process.version !== "v24.19.0" || hash(readFileSync(process.execPath)) !== nodeHash) throw new Error("Pinned Node runtime mismatch");
function baselineMetadata() {
  for (const file of baselinePins) if (hash(readFileSync(join(baseline, file.path))) !== file.sha256) throw new Error(`Frozen released baseline changed: ${file.path}`);
  const envelope = JSON.parse(readFileSync(join(baseline, "metadata.json"), "utf8"));
  const raw = envelope.metadata.find((entry) => entry.url === "https://registry.npmjs.org/@earendil-works%2fpi-ai/0.99.1");
  if (!raw || raw.status !== 200 || hash(raw.rawBody) !== raw.sha256 || raw.sha256 !== "ba57282ebb3656598d5a3e36de21b09f6a985fc98691412f042b83039566c864") throw new Error("Frozen pi-ai version metadata mismatch");
  const pkg = JSON.parse(raw.rawBody);
  if (pkg.name !== "@earendil-works/pi-ai" || pkg.version !== "0.99.1" || pkg.gitHead !== sourceSha || pkg.dist.tarball !== tarballUrl || pkg.dist.integrity !== integrity || pkg.dist.shasum !== shasum) throw new Error("Official package identity/integrity mismatch");
  return { raw, pkg };
}
function verifyTarball(bytes) {
  if (`sha512-${hash(bytes, "sha512", "base64")}` !== integrity || hash(bytes, "sha1") !== shasum) throw new Error("Official pi-ai tarball SRI/SHA-1 mismatch");
}
function download() {
  return new Promise((resolveDownload, reject) => {
    const req = get(tarballUrl, { headers: { "User-Agent": "PiSharp-public-package-baseline-research", Accept: "application/octet-stream", "Accept-Encoding": "identity" } }, (res) => {
      if (res.statusCode !== 200) { res.resume(); reject(new Error(`Official tarball status ${res.statusCode}; redirects are not followed`)); return; }
      const chunks = []; let count = 0;
      res.on("data", (chunk) => { count += chunk.length; if (count > 10 * 1024 * 1024) req.destroy(new Error("Package download exceeds byte limit")); else chunks.push(chunk); });
      res.on("error", reject);
      res.on("end", () => resolveDownload({ bytes: Buffer.concat(chunks), fetchedAt: new Date().toISOString(), status: res.statusCode, headers: { "content-type": res.headers["content-type"] ?? null, etag: res.headers.etag ?? null, "last-modified": res.headers["last-modified"] ?? null } }));
    });
    req.on("error", reject);
    req.setTimeout(20000, () => req.destroy(new Error("Read timeout")));
  });
}

// Authored parser copied from the frozen release inspection harness; no upstream code runs.
function parseTar(bytes, prefix) {
  const files = new Map(), kinds = {};
  let offset = 0, globalPax = {}, localPax = {}, metadataHeaders = 0;
  const str = (header, start, length) => header.subarray(start, start + length).toString("utf8").replace(/\0.*$/s, "");
  const octal = (header, start, length) => { const value = str(header, start, length).trim(); if (!/^[0-7]*$/.test(value)) throw new Error("Unsupported tar numeric encoding"); const parsed = value ? parseInt(value, 8) : 0; if (!Number.isSafeInteger(parsed)) throw new Error("Unsafe tar size"); return parsed; };
  function safeName(name, allowRoot = false) {
    if (name.includes("\\") || name.includes("\0") || name.startsWith("/") || /^[A-Za-z]:/.test(name)) throw new Error("Unsafe archive path");
    const trimmed = name.replace(/\/$/, "");
    if (trimmed.split("/").some((part) => !part || part === "." || part === "..")) throw new Error("Unsafe archive path component");
    if (!(name.startsWith(prefix) || (allowRoot && `${trimmed}/` === prefix))) throw new Error("Archive path outside expected root");
    return name.slice(prefix.length).replace(/\/$/, "");
  }
  function pax(data) {
    const attributes = {}; let index = 0;
    while (index < data.length) {
      const space = data.indexOf(32, index); if (space < 0) throw new Error("Invalid PAX record");
      const lengthText = data.subarray(index, space).toString("ascii"); if (!/^[1-9][0-9]*$/.test(lengthText)) throw new Error("Invalid PAX length");
      const length = Number(lengthText), end = index + length;
      if (!Number.isSafeInteger(length) || end > data.length || data[end - 1] !== 10) throw new Error("Invalid PAX bounds");
      const record = data.subarray(space + 1, end - 1).toString("utf8"), eq = record.indexOf("="); if (eq < 1) throw new Error("Invalid PAX attribute");
      const key = record.slice(0, eq); if (key.startsWith("GNU.sparse") || key === "size") throw new Error("Unsupported PAX sparse/size override");
      if (Object.hasOwn(attributes, key)) throw new Error("Duplicate PAX attribute");
      attributes[key] = record.slice(eq + 1); index = end;
    }
    return attributes;
  }
  while (offset + 512 <= bytes.length) {
    const header = bytes.subarray(offset, offset + 512);
    if (header.every((byte) => byte === 0)) { if (!bytes.subarray(offset).every((byte) => byte === 0)) throw new Error("Nonzero bytes after tar end"); if (Object.keys(localPax).length) throw new Error("Dangling PAX attributes"); return { files, kinds, metadataHeaders }; }
    const checksum = [...header].reduce((sum, byte, index) => sum + (index >= 148 && index < 156 ? 32 : byte), 0);
    if (checksum !== octal(header, 148, 8)) throw new Error("Tar header checksum mismatch");
    const size = octal(header, 124, 12), start = offset + 512, end = start + size;
    if (end > bytes.length) throw new Error("Truncated tar member");
    const data = bytes.subarray(start, end), type = str(header, 156, 1) || "0";
    offset = start + Math.ceil(size / 512) * 512;
    if (type === "x" || type === "g") { const values = pax(data); if (type === "g" && (values.path || values.linkpath)) throw new Error("Global PAX path override rejected"); if (type === "g") globalPax = { ...globalPax, ...values }; else localPax = values; metadataHeaders++; continue; }
    const attributes = { ...globalPax, ...localPax }; localPax = {};
    const name = attributes.path ?? [str(header, 345, 155), str(header, 0, 100)].filter(Boolean).join("/"), member = safeName(name, type === "5");
    if (member && files.has(member)) throw new Error(`Duplicate archive path: ${member}`);
    if (!["0", "5", "1", "2"].includes(type)) throw new Error(`Unsupported tar member type ${type}`);
    let link = null;
    if (type === "1" || type === "2") { link = attributes.linkpath ?? str(header, 157, 100); if (link.includes("\\") || link.startsWith("/") || /^[A-Za-z]:/.test(link) || link.includes("\0")) throw new Error("Unsafe archive link"); safeName(type === "1" ? link : posix.normalize(posix.join(posix.dirname(name), link))); if (size !== 0) throw new Error("Archive link has content"); }
    if (type === "5" && size !== 0) throw new Error("Directory has content");
    kinds[type] = (kinds[type] ?? 0) + 1;
    if (member) files.set(member, { type, data, link });
  }
  throw new Error("Tar has no complete end marker");
}

function inspect() {
  const { raw, pkg: official } = baselineMetadata();
  const bytes = readFileSync(join(root, "pi-ai-0.99.1.tgz")); verifyTarball(bytes);
  const acquisition = JSON.parse(readFileSync(join(root, "acquisition.json"), "utf8"));
  if (acquisition.url !== tarballUrl || acquisition.status !== 200 || acquisition.bytes !== bytes.length || acquisition.sha256 !== hash(bytes) || acquisition.integrity !== integrity || acquisition.shasum !== shasum || acquisition.metadataSha256 !== raw.sha256) throw new Error("Frozen acquisition record mismatch");
  const archive = parseTar(gunzipSync(bytes, { maxOutputLength: 32 * 1024 * 1024 }), "package/");
  const sourceArchive = parseTar(gunzipSync(readFileSync(join(baseline, "pi-0.99.1-source.tar.gz")), { maxOutputLength: 256 * 1024 * 1024 }), "pi-0.99.1/");
  const releaseInspection = JSON.parse(readFileSync(join(baseline, "inspection.json"), "utf8"));
  const regularFiles = [...archive.files].filter(([, file]) => file.type === "0");
  const unpackedBytes = regularFiles.reduce((sum, [, file]) => sum + file.data.length, 0);
  if (regularFiles.length !== official.dist.fileCount || unpackedBytes !== official.dist.unpackedSize) throw new Error("Official package file count/unpacked size mismatch");
  const packageBytes = archive.files.get("package.json")?.data;
  if (!packageBytes) throw new Error("Packaged package.json absent");
  const pkg = JSON.parse(packageBytes.toString("utf8"));
  if (pkg.name !== official.name || pkg.version !== official.version || pkg.license !== official.license) throw new Error("Packaged identity/license declaration mismatch");
  const sourcePackageBytes = sourceArchive.files.get("packages/ai/package.json").data;
  const sourcePackage = JSON.parse(sourcePackageBytes.toString("utf8"));
  const sourcePrefix = "packages/ai/src/providers/data/", packagePrefix = "dist/providers/data/";
  const expected = [...sourceArchive.files].filter(([path, file]) => file.type === "0" && path.startsWith(sourcePrefix)).map(([path]) => path.slice(sourcePrefix.length)).sort();
  const actual = [...archive.files].filter(([path, file]) => file.type === "0" && path.startsWith(packagePrefix)).map(([path]) => path.slice(packagePrefix.length)).sort();
  const comparisons = [...new Set([...expected, ...actual])].sort().map((name) => {
    const source = sourceArchive.files.get(`${sourcePrefix}${name}`), packaged = archive.files.get(`${packagePrefix}${name}`);
    return { name, sourcePresent: !!source, packagePresent: !!packaged, sourceBytes: source?.data.length ?? null, packagedBytes: packaged?.data.length ?? null, sourceSha256: source ? hash(source.data) : null, packagedSha256: packaged ? hash(packaged.data) : null, byteIdentical: !!source && !!packaged && source.data.equals(packaged.data) };
  });
  const manifestBytes = archive.files.get(`${packagePrefix}.manifest.json`)?.data;
  const manifest = manifestBytes ? JSON.parse(manifestBytes.toString("utf8")) : null;
  const packagedProviderNames = actual.filter((name) => name !== ".manifest.json");
  const manifestMatches = manifest !== null && manifest.schemaVersion === 6 && JSON.stringify(Object.keys(manifest.files).sort()) === JSON.stringify(packagedProviderNames) && packagedProviderNames.every((name) => manifest.files[name] === hash(archive.files.get(`${packagePrefix}${name}`).data));
  const notices = regularFiles.filter(([path]) => /(^|\/)(license|licence|copying|notice|third[-_]party[-_]notices)(\.[^/]*)?$/i.test(path)).map(([path, file]) => ({ path, bytes: file.data.length, sha256: hash(file.data), utf8Text: file.data.toString("utf8") }));
  const license = archive.files.get("LICENSE")?.data, sourceLicense = sourceArchive.files.get("LICENSE").data;
  const declarations = { dependencies: pkg.dependencies ?? {}, optionalDependencies: pkg.optionalDependencies ?? {}, peerDependencies: pkg.peerDependencies ?? {}, peerDependenciesMeta: pkg.peerDependenciesMeta ?? {}, bundledDependencies: pkg.bundledDependencies ?? pkg.bundleDependencies ?? [], engines: pkg.engines ?? {} };
  const dependencyDeclarationsEqualPinnedSource = ["dependencies", "optionalDependencies", "peerDependencies", "peerDependenciesMeta", "bundledDependencies", "bundleDependencies", "engines"].every((key) => JSON.stringify(pkg[key]) === JSON.stringify(sourcePackage[key]));
  const fileManifest = regularFiles.map(([path, file]) => ({ path, bytes: file.data.length, sha256: hash(file.data) })).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  return { schemaVersion: 1, kind: "official-pi-ai-release-package-inspection", package: { name: official.name, version: official.version, gitHeadDeclaredByRegistry: official.gitHead, registryMetadataSha256: raw.sha256, url: tarballUrl, bytes: bytes.length, sha256: hash(bytes), integrity, shasum, sha512AndSha1Verified: true, registryReportedFiles: official.dist.fileCount, observedRegularFiles: regularFiles.length, registryReportedUnpackedBytes: official.dist.unpackedSize, observedUnpackedBytes: unpackedBytes, packageJsonBytes: packageBytes.length, packageJsonSha256: hash(packageBytes), packageJsonEqualsSourceBytes: packageBytes.equals(sourcePackageBytes), signatureVerified: false, attestationVerified: false }, archiveSafety: { extracted: false, executed: false, memberKinds: archive.kinds, paxMetadataHeaders: archive.metadataHeaders, confinedRoot: "package/", absoluteTraversalAndEscapingLinksRejected: true }, releaseSourceAuthority: { sourceSha, archiveSha256: baselinePins[3].sha256, evidenceCommit: "f6ccac5b077b25ab76d012545eb771197a4540e4", checksumAttributeCommit: "70c7b18de6a6b95b9e2d927775483117390de5d1" }, catalogComparison: { sourcePath: sourcePrefix, packagePath: packagePrefix, sourceFiles: expected.length, packagedFiles: actual.length, allSourceAndPackageCatalogBytesIdentical: comparisons.length === expected.length && comparisons.every((file) => file.byteIdentical), comparedFiles: comparisons, packagedManifestPresent: manifest !== null, packagedManifestHashesMatched: manifestMatches, sourceCatalogProviders: releaseInspection.catalog.providers, sourceCatalogModels: releaseInspection.catalog.models, sourceGeneratedAt: releaseInspection.catalog.generatedAt, packagedGeneratedAt: manifest?.generatedAt ?? null, sourceStructureHash: releaseInspection.catalog.structureHash, packagedStructureHash: manifest?.structureHash ?? null, reconstructed: false, rawCatalogNumericLexemesRetainedInTarball: true }, notices, licenseComparison: { packagedLicensePresent: !!license, packagedLicenseEqualsPinnedRootLicense: !!license && license.equals(sourceLicense), packagedLicenseSha256: license ? hash(license) : null, packagedLicenseDeclaration: pkg.license, transitiveAndGeneratedDataLicensingReviewed: false }, dependencyDeclarations: declarations, dependencyDeclarationsEqualPinnedSource, packageScriptsObservedNotExecuted: pkg.scripts ?? {}, packagedFiles: fileManifest, limitations: ["Only pi-ai0.99.1 tarball acquired; other public packages and their exact transitive dependencies remain unacquired/unqualified.", "Registry signatures and provenance URLs are recorded in the earlier metadata evidence but not cryptographically verified here.", "Npm gitHead/license fields are declarations; source build reproducibility and complete dependency/data/native asset licensing are not established.", "Tarball/catalog byte identity does not validate SDK/provider execution, model access, current rates, or full native/runtime parity.", "Source and package archives stay inert and compressed; no install, extraction, resolution, scripts, provider endpoints or credentials are used."] };
}

if (mode === "--acquire-new") {
  for (const name of ["pi-ai-0.99.1.tgz", "acquisition.json", "inspection.json", "evidence.lock.json"]) if (existsSync(join(root, name))) throw new Error(`Refusing to overwrite ${name}`);
  const { raw } = baselineMetadata(), result = await download(); verifyTarball(result.bytes);
  writeFileSync(join(root, "pi-ai-0.99.1.tgz"), result.bytes, { flag: "wx" });
  writeFileSync(join(root, "acquisition.json"), JSON.stringify({ schemaVersion: 1, mode: "anonymous-read-only", url: tarballUrl, fetchedAt: result.fetchedAt, status: result.status, headers: result.headers, bytes: result.bytes.length, sha256: hash(result.bytes), integrity, shasum, metadataSha256: raw.sha256, installed: false, extracted: false, executed: false }, null, 2) + "\n", { flag: "wx" });
  console.log(JSON.stringify({ acquired: true, bytes: result.bytes.length, sha256: hash(result.bytes), integrityVerified: true, shasumVerified: true }));
} else if (mode === "--inspect-new") {
  for (const name of ["inspection.json", "evidence.lock.json"]) if (existsSync(join(root, name))) throw new Error(`Refusing to overwrite ${name}`);
  const report = inspect();
  writeFileSync(join(root, "inspection.json"), JSON.stringify(report, null, 2) + "\n", { flag: "wx" });
  const files = frozenNames.map((path) => { const bytes = readFileSync(join(root, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; });
  writeFileSync(join(root, "evidence.lock.json"), JSON.stringify({ schemaVersion: 1, sourceSha, createdAt: new Date().toISOString(), node: { version: process.version, sha256: nodeHash }, baselineFiles: baselinePins, files, mode: "default offline verification; first-new modes refuse overwrite; archives never extracted/executed" }, null, 2) + "\n", { flag: "wx" });
  console.log(JSON.stringify({ frozen: true, packageFiles: report.package.observedRegularFiles, catalogFiles: report.catalogComparison.packagedFiles, catalogByteIdentity: report.catalogComparison.allSourceAndPackageCatalogBytesIdentical, licenseByteIdentity: report.licenseComparison.packagedLicenseEqualsPinnedRootLicense }));
} else {
  const lock = JSON.parse(readFileSync(join(root, "evidence.lock.json"), "utf8"));
  if (lock.sourceSha !== sourceSha || lock.node.version !== process.version || lock.node.sha256 !== nodeHash || JSON.stringify(lock.baselineFiles) !== JSON.stringify(baselinePins) || JSON.stringify(lock.files.map((file) => file.path)) !== JSON.stringify(frozenNames)) throw new Error("Frozen evidence lock identity mismatch");
  for (const file of lock.files) { const bytes = readFileSync(join(root, file.path)); if (bytes.length !== file.bytes || hash(bytes) !== file.sha256) throw new Error(`Immutable npm evidence changed: ${file.path}`); }
  const actual = JSON.stringify(inspect(), null, 2) + "\n";
  if (actual !== readFileSync(join(root, "inspection.json"), "utf8")) throw new Error("Offline npm inspection differs from frozen output");
  const report = JSON.parse(actual);
  console.log(JSON.stringify({ verified: true, immutableFiles: lock.files.length, frozenBaselineFiles: baselinePins.length, tarballSha256: report.package.sha256, packageFiles: report.package.observedRegularFiles, catalogFiles: report.catalogComparison.packagedFiles, catalogByteIdentity: report.catalogComparison.allSourceAndPackageCatalogBytesIdentical, licenseByteIdentity: report.licenseComparison.packagedLicenseEqualsPinnedRootLicense }));
}
