// PiSharp Node bridge host hook helpers shared by the virtual Pi modules.
//
// Runtime values that need the live PiSharp host (model catalog, provider streams, built-in tools,
// clipboard, sessions, ...) call `globalThis.__pisharpBridge.call(name, ...args)`. When the hook is not
// installed, they throw `Error("<name> is not available in the PiSharp Node bridge")`, so module linking
// always succeeds and only actual use fails.

/** Marker on stub exports: "host" (forwards to the hook) or "unavailable" (always throws). */
export const STUB = Symbol.for("pisharp.bridge.stub");

export function unavailable(name) {
	return new Error(`${name} is not available in the PiSharp Node bridge`);
}

export function getBridge() {
	const bridge = globalThis.__pisharpBridge;
	return bridge && typeof bridge.call === "function" ? bridge : undefined;
}

/** Call the host hook `name` with `args`, or throw when the hook is absent. */
export function hostCall(name, ...args) {
	const bridge = getBridge();
	if (!bridge) throw unavailable(name);
	return bridge.call(name, ...args);
}

/** A function export that forwards to the host hook under `name`. */
export function hostFunction(name) {
	const fn = (...args) => hostCall(name, ...args);
	Object.defineProperty(fn, "name", { value: name });
	Object.defineProperty(fn, STUB, { value: "host" });
	return fn;
}

/** A function export that always throws (no host equivalent). */
export function unavailableFunction(name) {
	const fn = () => {
		throw unavailable(name);
	};
	Object.defineProperty(fn, "name", { value: name });
	Object.defineProperty(fn, STUB, { value: "unavailable" });
	return fn;
}

const PASSTHROUGH = new Set(["prototype", "name", "length", "toString", "constructor", "then", "valueOf"]);

/**
 * A class export whose construction and static method calls throw. `instanceof` checks keep working
 * (always false), so code that merely references the class links and runs.
 */
export function unavailableClass(name) {
	const Stub = class {
		constructor() {
			throw unavailable(name);
		}
	};
	Object.defineProperty(Stub, "name", { value: name });
	Object.defineProperty(Stub, STUB, { value: "unavailable" });
	return new Proxy(Stub, {
		get(target, prop, receiver) {
			if (typeof prop === "symbol" || PASSTHROUGH.has(prop) || prop in target) return Reflect.get(target, prop, receiver);
			return () => {
				throw unavailable(`${name}.${prop}`);
			};
		},
	});
}

/** An object export whose method calls are forwarded to the host as `<name>.<method>`. */
export function hostObject(name) {
	return new Proxy(
		{},
		{
			get(_target, prop) {
				if (typeof prop === "symbol" || prop === "then" || prop === "toJSON") return undefined;
				return (...args) => hostCall(`${name}.${prop}`, ...args);
			},
		},
	);
}
