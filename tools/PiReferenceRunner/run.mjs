import { createHash } from "node:crypto";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";
import { compareJson, NORMALIZER_VERSION } from "../CompatibilityReport/canonical-json.mjs";
import { parseJsonSupported } from "../CompatibilityReport/raw-json.mjs";

const toolRoot = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(toolRoot, "../..");
const scratchRoot = join(repoRoot, "artifacts", "reference-temp");
const args = process.argv.slice(2);
function argument(name) { const i = args.indexOf(name); return i < 0 ? undefined : args[i + 1]; }
const upstreamRoot = argument("--upstream");
const outputRoot = argument("--out-dir");
if (!upstreamRoot) throw new Error("Usage: node tools/PiReferenceRunner/run.mjs --upstream PINNED_CHECKOUT [--out-dir DIRECTORY]");
const baseline = parseJsonSupported(readFileSync(join(repoRoot, "compatibility/baseline.lock.json"), "utf8"));
function git(...gitArgs) {
  const result = spawnSync("git", ["-c", `safe.directory=${resolve(upstreamRoot)}`, "-C", upstreamRoot, ...gitArgs], { encoding: "utf8", windowsHide: true });
  if (result.status !== 0) throw new Error(`Git baseline check failed: ${result.stderr}`);
  return result.stdout.trim();
}
if (git("rev-parse", "HEAD") !== baseline.source.commit) throw new Error("Reference checkout SHA does not match baseline");
if (git("status", "--porcelain", "--untracked-files=all") !== "") throw new Error("Reference checkout must be unmodified");
const hash = path => createHash("sha256").update(readFileSync(path)).digest("hex");
const sourcePath = "packages/ai/src/utils/event-stream.ts";
const sourceArtifact = baseline.artifacts.find(artifact => artifact.path === sourcePath);
if (hash(join(upstreamRoot, sourcePath)) !== sourceArtifact.checkoutSha256) throw new Error("Reference module bytes differ from locked checkout artifact");
if (hash(process.execPath) !== baseline.referenceRuntime.sha256 || process.version !== baseline.referenceRuntime.version) throw new Error("Reference Node executable does not match locked runtime");
const manifest = parseJsonSupported(readFileSync(join(repoRoot, "fixtures/pi-v0.99.1/manifest.json"), "utf8"));
const rows = manifest.fixtures.filter(fixture => fixture.provenance.kind === "captured-upstream-oracle");
const results = [];
mkdirSync(scratchRoot, { recursive: true });
for (const row of rows) {
  const inputPath = join(repoRoot, row.input.path);
  const expectedPath = join(repoRoot, row.expected.path);
  if (hash(inputPath) !== row.input.sha256 || hash(expectedPath) !== row.expected.sha256) throw new Error(`Fixture checksum mismatch: ${row.fixtureId}`);
  const outputs = [];
  for (let repeat = 0; repeat < 2; repeat++) {
    const isolatedRoot = mkdtempSync(join(scratchRoot, "pisharp-reference-"));
    try {
      const home = join(isolatedRoot, "home");
      const workspace = join(isolatedRoot, "workspace");
      mkdirSync(home); mkdirSync(workspace);
      const child = spawnSync(process.execPath, ["--experimental-strip-types", "--import", pathToFileURL(join(toolRoot, "offline-guard.mjs")).href, join(toolRoot, "capture.mjs"), resolve(upstreamRoot), inputPath], {
        cwd: workspace,
        env: { SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR, USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home, TMP: isolatedRoot, TEMP: isolatedRoot, TZ: "UTC", PI_CODING_AGENT_DIR: home },
        encoding: "utf8", timeout: 10000, maxBuffer: 1024 * 1024, windowsHide: true,
      });
      if (child.status !== 0) throw new Error(`Reference capture failed: ${child.error?.message ?? child.stderr}`);
      outputs.push(parseJsonSupported(child.stdout));
    } finally {
      const cleanupTarget = resolve(isolatedRoot);
      const withinTemporaryRoot = relative(resolve(scratchRoot), cleanupTarget);
      if (!withinTemporaryRoot || isAbsolute(withinTemporaryRoot) || withinTemporaryRoot.startsWith("..")) throw new Error("Refusing cleanup outside the temporary root");
      rmSync(cleanupTarget, { recursive: true, force: true });
    }
  }
  const expected = parseJsonSupported(readFileSync(expectedPath, "utf8"));
  const deterministic = compareJson(outputs[0], outputs[1]);
  const matched = compareJson(expected, outputs[0]);
  if (outputRoot) { mkdirSync(outputRoot, { recursive: true }); writeFileSync(join(outputRoot, `${row.fixtureId}.actual.json`), `${JSON.stringify(outputs[0], null, 2)}\n`); }
  results.push({ fixtureId: row.fixtureId, deterministic, matched, inputSha256: row.input.sha256, expectedSha256: row.expected.sha256 });
}
const report = { schemaVersion: 1, sourceSha: baseline.source.commit, sourceModule: sourcePath, sourceModuleSha256: sourceArtifact.checkoutSha256, runtime: baseline.referenceRuntime, normalizerVersion: NORMALIZER_VERSION, scope: "upstream event queue and terminal settlement only; no provider, frame reduction or native parity claim", credentialEnvironment: "not inherited", network: "disabled by child builtin guard; dependency-free source seam only", runsPerFixture: 2, results };
if (outputRoot) writeFileSync(join(outputRoot, "reference-report.json"), `${JSON.stringify(report, null, 2)}\n`);
console.log(JSON.stringify(report, null, 2));
if (results.length === 0 || results.some(row => !row.matched || !row.deterministic)) process.exitCode = 1;
