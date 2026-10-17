// The upstream pins the durable capture scripts accept, and the argument handling they share. Every capture script takes the Pi
// checkout and the commit it must be at (`--commit <sha>`): the checkout's HEAD must be that commit and its working tree clean
// (node_modules is ignored by Pi's .gitignore, so a checkout with node_modules linked in counts as clean). The commit also selects
// what the scripts write: tool results are `content` at v1.1.0 and `output` from v1.2.0, scenarios new in v1.2.0 run only there,
// and the default output location is the commit's fixture set.
//
// Fixture sets (tests/PiSharp.Durable.Tests/Fixtures):
//   1.1.0  Fixtures/*.upstream.json(l) and Fixtures/upstream-sessions, which the C# suites compare with today;
//   1.2.0  Fixtures/v1.2.0/*.upstream.json(l) and Fixtures/v1.2.0/upstream-sessions, which each work-list area adopts (see
//          Fixtures/v1.2.0/index.json).
import { execFileSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const PINS = {
  '1.1.0': 'abe508e1b89912adde45528136c3221eb69acdd7',
  '1.2.0': '06a466935536f8c9bcacb2d5efc0fc021be1bdff',
};

export const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const fixtures = join(repository, 'tests', 'PiSharp.Durable.Tests', 'Fixtures');

/** Where a version's fixtures live: the 1.1.0 set stays where the C# suites read it; later sets get a folder of their own. */
export function fixtureDirectory(version) {
  return version === '1.1.0' ? fixtures : join(fixtures, `v${version}`);
}

/** The version a pinned commit is, or an error naming the pins. */
export function versionOf(commit) {
  const found = Object.entries(PINS).find(([, sha]) => sha === commit);
  if (found === undefined) {
    throw new Error(`${commit} is not a pinned commit; pins: ${Object.entries(PINS).map(([v, sha]) => `${v} ${sha}`).join(', ')}`);
  }
  return found[0];
}

/** Fails unless the checkout is at `commit` with a clean working tree. */
export function checkCheckout(pi, commit) {
  const head = execFileSync('git', ['-C', pi, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  if (head !== commit) throw new Error(`The Pi checkout ${pi} is at ${head}, not the pinned ${commit}`);
  const dirty = execFileSync('git', ['-C', pi, 'status', '--porcelain'], { encoding: 'utf8' }).trim();
  if (dirty !== '') throw new Error(`The Pi checkout ${pi} has local changes:\n${dirty}`);
}

/**
 * Parses `<pi checkout> --commit <sha> [positional...]` (the flag may come anywhere) and checks the checkout. Without `--commit`,
 * `fallback` is used when given (verify-reverse.mjs keeps its three-argument form for the C# reverse case), else it is an error.
 * Returns the checkout, the commit, its version, whether tool results use `output` (v1.2.0 and later), and the other positionals.
 */
export function pinnedArguments(argv, usage, fallback) {
  const positional = [];
  let commit;
  for (let index = 0; index < argv.length; index++) {
    if (argv[index] === '--commit') commit = argv[++index];
    else positional.push(argv[index]);
  }
  commit ??= fallback;
  const [pi, ...rest] = positional;
  if (!pi || !commit) throw new Error(`usage: ${usage}`);
  const version = versionOf(commit);
  checkCheckout(pi, commit);
  return { pi: resolve(pi), commit, version, output: version !== '1.1.0', rest };
}

/** A tool result in the pinned version's shape: `content` at v1.1.0, `output` from v1.2.0. */
export function toolResult(pin, content, rest = {}) {
  return pin.output ? { output: content, ...rest } : { content, ...rest };
}
