// Find the source lock that actually produced an inventory or published release.
// A newer candidate source target never upgrades historical evidence implicitly.
export function resolveSourceLock(read, target, commit) {
  const pending = [target], seen = new Set();
  while (pending.length) {
    const lock = pending.shift();
    if (seen.has(lock.source.commit)) continue;
    seen.add(lock.source.commit);
    if (lock.source.commit === commit) return lock;
    for (const link of [lock.previousTarget, lock.previousBaseline]) if (link) {
      const previous = read(link.lock);
      if (previous.source.commit !== link.commit || previous.source.tag !== link.tag)
        throw new Error(`Preserved source lock does not match its link: ${link.lock}`);
      pending.push(previous);
    }
  }
  throw new Error(`No preserved source lock for ${commit}`);
}
