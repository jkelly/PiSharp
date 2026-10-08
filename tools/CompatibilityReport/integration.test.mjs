import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import test from "node:test";

const toolRoot = dirname(fileURLToPath(import.meta.url));
const repo = resolve(toolRoot, "../..");
const scratchRoot = join(repo, "artifacts", "comparator-temp");
mkdirSync(scratchRoot, { recursive: true });

test("CLI comparison detects deliberate mutation of fixture events and rejects invalid evidence", () => {
  const scratch = mkdtempSync(join(scratchRoot, "mutation-"));
  const expected = join(repo, "fixtures/pi-v0.99.1/providers/text-authoritative-end.expected.json");
  try {
    const actual = join(scratch, "actual.json");
    const original = readFileSync(expected, "utf8");
    const compare = () => spawnSync(process.execPath, [join(toolRoot, "compare.mjs"), expected, actual], { encoding: "utf8", windowsHide: true });
    writeFileSync(actual, original);
    assert.equal(compare().status, 0);
    const changed = JSON.parse(original);
    changed.events.reverse();
    writeFileSync(actual, `${JSON.stringify(changed)}\n`);
    const mismatch = compare();
    assert.equal(mismatch.status, 1);
    assert.equal(JSON.parse(mismatch.stdout).equal, false);
    writeFileSync(actual, '{"duplicate":1,"duplicate":2}');
    const invalid = compare();
    assert.equal(invalid.status, 2);
    assert.match(invalid.stderr, /Duplicate/);
  } finally {
    const cleanupTarget = resolve(scratch);
    const withinScratchRoot = relative(resolve(scratchRoot), cleanupTarget);
    if (!withinScratchRoot || isAbsolute(withinScratchRoot) || withinScratchRoot.startsWith("..")) throw new Error("Refusing cleanup outside comparator scratch root");
    rmSync(cleanupTarget, { recursive: true, force: true });
  }
});
