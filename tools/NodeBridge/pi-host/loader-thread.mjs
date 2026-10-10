// Loader-thread form of ./loader-hooks.mjs for Node versions without module.registerHooks (module.register, Node 20.6+).
import { resolve as resolveSync, load as loadSync } from './loader-hooks.mjs';

export async function resolve(specifier, context, nextResolve) { return resolveSync(specifier, context, nextResolve); }
export async function load(url, context, nextLoad) { return loadSync(url, context, nextLoad); }
