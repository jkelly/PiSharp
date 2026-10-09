// PiSharp native replacement for `typebox/value` (TypeBox 1.x Value API). Check/Errors/Assert/Parse use
// the shared validator; Convert/Default/Clean/Create follow TypeBox 1.x semantics (Convert and Clean act on
// TypeBox-built schemas, identified by their '~kind' marker, exactly as TypeBox does).
import { check as validatorCheck, deepEqual, errors as validatorErrors, isObjectNotArray } from "./typebox-validator.mjs";

const kind = (schema) => (schema && typeof schema === "object" ? schema["~kind"] : undefined);
const isOptional = (schema) => !!schema && typeof schema === "object" && schema["~optional"] === true;

/** Split TypeBox's overloads: (schema, value) or (context, schema, value). */
function args(list) {
	return list.length >= 3 ? { context: list[0], schema: list[1], value: list[2] } : { context: undefined, schema: list[0], value: list[1] };
}

function resolveRef(context, schema) {
	const ref = schema.$ref;
	if (context && Object.hasOwn(context, ref)) return context[ref];
	return undefined;
}

export class AssertError extends Error {
	constructor(source, value, errors) {
		super(source);
		this.cause = { source, errors, value };
	}
}

export class ParseError extends AssertError {
	constructor(value, errors) {
		super("Parse", value, errors);
	}
}

export class CreateError extends Error {}

export class DecodeError extends AssertError {
	constructor(value, errors) {
		super("Decode", value, errors);
	}
}

export class EncodeError extends AssertError {
	constructor(value, errors) {
		super("Encode", value, errors);
	}
}

export function Clone(value) {
	if (value === null || typeof value !== "object") return value;
	if (Array.isArray(value)) return value.map(Clone);
	if (value instanceof Date) return new Date(value.getTime());
	if (ArrayBuffer.isView(value)) return value.slice();
	if (value instanceof Map) return new Map([...value].map(([k, v]) => [k, Clone(v)]));
	if (value instanceof Set) return new Set([...value].map(Clone));
	const result = Object.create(Object.getPrototypeOf(value));
	for (const key of Object.keys(value)) result[key] = Clone(value[key]);
	return result;
}

export function Check(...list) {
	const { context, schema, value } = args(list);
	return validatorCheck(schema, value, context);
}

export function Errors(...list) {
	const { context, schema, value } = args(list);
	return validatorErrors(schema, value, context);
}

export function Assert(...list) {
	const { context, schema, value } = args(list);
	if (!validatorCheck(schema, value, context)) throw new AssertError("Assert", value, validatorErrors(schema, value, context));
}

// ---------------------------------------------------------------------------------------------
// Convert
// ---------------------------------------------------------------------------------------------

const ok = (value) => ({ ok: true, value });
const fail = { ok: false };

function tryNumber(value) {
	if (typeof value === "bigint") return value <= BigInt(Number.MAX_SAFE_INTEGER) && value >= BigInt(Number.MIN_SAFE_INTEGER) ? ok(Number(value)) : fail;
	if (typeof value === "boolean") return ok(value ? 1 : 0);
	if (typeof value === "number") return ok(value);
	if (value === null || value === undefined) return ok(0);
	if (typeof value === "string") {
		const coerced = +value;
		if (Number.isFinite(coerced)) return ok(coerced);
		const lower = value.toLowerCase();
		if (lower === "false") return ok(0);
		if (lower === "true") return ok(1);
		try {
			const big = BigInt(value);
			return big <= BigInt(Number.MAX_SAFE_INTEGER) && big >= BigInt(Number.MIN_SAFE_INTEGER) ? ok(Number(big)) : fail;
		} catch {
			return fail;
		}
	}
	return fail;
}

function tryBoolean(value) {
	if (typeof value === "bigint") return value === 0n ? ok(false) : value === 1n ? ok(true) : fail;
	if (typeof value === "boolean") return ok(value);
	if (typeof value === "number") return value === 0 ? ok(false) : value === 1 ? ok(true) : fail;
	if (value === null || value === undefined) return ok(false);
	if (typeof value === "string") {
		const lower = value.toLowerCase();
		if (lower === "false" || value === "0") return ok(false);
		if (lower === "true" || value === "1") return ok(true);
	}
	return fail;
}

function tryString(value) {
	if (typeof value === "bigint" || typeof value === "boolean" || typeof value === "number") return ok(value.toString());
	if (value === null) return ok("null");
	if (typeof value === "string") return ok(value);
	if (value === undefined) return ok("");
	return fail;
}

function tryNull(value) {
	if (typeof value === "bigint") return value === 0n ? ok(null) : fail;
	if (typeof value === "boolean") return value === false ? ok(null) : fail;
	if (typeof value === "number") return value === 0 ? ok(null) : fail;
	if (value === null || value === undefined) return ok(null);
	if (typeof value === "string") {
		const lower = value.toLowerCase();
		return lower === "undefined" || lower === "null" || value === "" || value === "0" ? ok(null) : fail;
	}
	return fail;
}

function tryUndefined(value) {
	if (typeof value === "bigint") return value === 0n ? ok(undefined) : fail;
	if (typeof value === "boolean") return value === false ? ok(undefined) : fail;
	if (typeof value === "number") return value === 0 ? ok(undefined) : fail;
	if (value === null || value === undefined) return ok(undefined);
	if (typeof value === "string") {
		const lower = value.toLowerCase();
		return lower === "undefined" || lower === "null" || value === "" || value === "0" ? ok(undefined) : fail;
	}
	return fail;
}

function convertLiteral(constant, value) {
	const t = typeof constant;
	const r = t === "boolean" ? tryBoolean(value) : t === "number" ? tryNumber(value) : t === "string" ? tryString(value) : fail;
	return r.ok && deepEqual(constant, r.value) ? r : fail;
}

function convertType(context, schema, value) {
	switch (kind(schema)) {
		case "Array": {
			const array = Array.isArray(value) ? value : [value];
			return array.map((item) => convertType(context, schema.items, item));
		}
		case "BigInt": {
			if (typeof value === "bigint") return value;
			try {
				return BigInt(typeof value === "number" ? Math.trunc(value) : value);
			} catch {
				return value;
			}
		}
		case "Boolean": {
			const r = tryBoolean(value);
			return r.ok ? r.value : value;
		}
		case "Enum": {
			if (schema.enum.some((option) => deepEqual(option, value))) return value;
			for (const option of schema.enum) {
				const converted = convertLiteral(option, value);
				if (converted.ok) return converted.value;
			}
			return value;
		}
		case "Integer": {
			const r = tryNumber(value);
			return r.ok ? Math.trunc(r.value) : value;
		}
		case "Intersect": {
			let result = value;
			for (const sub of schema.allOf) result = convertType(context, sub, result);
			return result;
		}
		case "Literal": {
			if (deepEqual(schema.const, value)) return value;
			const r = convertLiteral(schema.const, value);
			return r.ok ? r.value : value;
		}
		case "Null": {
			const r = tryNull(value);
			return r.ok ? r.value : value;
		}
		case "Number": {
			const r = tryNumber(value);
			return r.ok ? r.value : value;
		}
		case "Object":
		case "Record": {
			if (!isObjectNotArray(value)) return value;
			const entries = [
				...Object.entries(schema.properties ?? {}).map(([key, sub]) => [new RegExp(`^${key.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`), sub]),
				...Object.entries(schema.patternProperties ?? {}).map(([pattern, sub]) => [new RegExp(pattern), sub]),
			];
			const keys = Object.keys(value);
			for (const [re, sub] of entries) {
				for (const key of keys) {
					if (!re.test(key) || (value[key] === undefined && isOptional(sub))) continue;
					value[key] = convertType(context, sub, value[key]);
				}
			}
			if (schema.additionalProperties && typeof schema.additionalProperties === "object") {
				for (const key of keys) {
					if (!entries.some(([re]) => re.test(key))) value[key] = convertType(context, schema.additionalProperties, value[key]);
				}
			}
			return value;
		}
		case "Ref": {
			const target = resolveRef(context, schema);
			return target ? convertType(context, target, value) : value;
		}
		case "String": {
			const r = tryString(value);
			return r.ok ? r.value : value;
		}
		case "Tuple": {
			if (!Array.isArray(value)) return value;
			return value.map((item, i) => (i < schema.items.length ? convertType(context, schema.items[i], item) : item));
		}
		case "Undefined":
		case "Void": {
			const r = tryUndefined(value);
			return r.ok ? r.value : value;
		}
		case "Union": {
			if (schema.anyOf.some((sub) => validatorCheck(sub, value, context))) return value;
			const candidates = schema.anyOf.map((sub) => convertType(context, sub, Clone(value)));
			const selected = candidates.find((candidate) => validatorCheck(schema, candidate, context));
			return selected === undefined ? value : selected;
		}
		default:
			return value;
	}
}

export function Convert(...list) {
	const { context, schema, value } = args(list);
	return convertType(context, schema, value);
}

// ---------------------------------------------------------------------------------------------
// Default
// ---------------------------------------------------------------------------------------------

function defaultType(context, schema, value) {
	if (!schema || typeof schema !== "object") return value;
	let current = value;
	if ("default" in schema && current === undefined) {
		current = typeof schema.default === "function" ? schema.default() : Clone(schema.default);
	}
	switch (kind(schema)) {
		case "Array":
			if (Array.isArray(current)) for (let i = 0; i < current.length; i++) current[i] = defaultType(context, schema.items, current[i]);
			return current;
		case "Intersect": {
			let result = current;
			for (const sub of schema.allOf) result = defaultType(context, sub, result);
			return result;
		}
		case "Object": {
			if (!(typeof current === "object" && current !== null)) return current;
			const known = Object.keys(schema.properties ?? {});
			for (const key of known) {
				const sub = schema.properties[key];
				const propertyValue = defaultType(context, sub, current[key]);
				if (propertyValue === undefined && (isOptional(sub) || !Object.hasOwn(sub, "default"))) continue;
				current[key] = propertyValue;
			}
			if (!("additionalProperties" in schema) || typeof schema.additionalProperties === "boolean") return current;
			for (const key of Object.keys(current)) {
				if (!known.includes(key)) current[key] = defaultType(context, schema.additionalProperties, current[key]);
			}
			return current;
		}
		case "Record": {
			if (!isObjectNotArray(current)) return current;
			const [pattern, sub] = Object.entries(schema.patternProperties ?? {})[0] ?? [];
			if (!sub) return current;
			const re = new RegExp(pattern);
			for (const key of Object.keys(current)) if (re.test(key)) current[key] = defaultType(context, sub, current[key]);
			return current;
		}
		case "Ref": {
			const target = resolveRef(context, schema);
			return target ? defaultType(context, target, current) : current;
		}
		case "Tuple":
			if (Array.isArray(current)) for (let i = 0; i < schema.items.length; i++) current[i] = defaultType(context, schema.items[i], current[i]);
			return current;
		case "Union":
			for (const sub of schema.anyOf) {
				const result = defaultType(context, sub, Clone(current));
				if (validatorCheck(sub, result, context)) return result;
			}
			return current;
		default:
			return current;
	}
}

export function Default(...list) {
	const { context, schema, value } = args(list);
	return defaultType(context, schema, value);
}

// ---------------------------------------------------------------------------------------------
// Clean
// ---------------------------------------------------------------------------------------------

function cleanType(context, schema, value) {
	switch (kind(schema)) {
		case "Array":
			return Array.isArray(value) ? value.map((item) => cleanType(context, schema.items, item)) : value;
		case "Intersect": {
			if (!isObjectNotArray(value)) return value;
			const known = new Set();
			for (const sub of schema.allOf) for (const key of Object.keys(sub.properties ?? {})) known.add(key);
			for (const key of Object.keys(value)) {
				if (!known.has(key) && !(schema.unevaluatedProperties === true)) delete value[key];
			}
			for (const sub of schema.allOf) {
				for (const [key, prop] of Object.entries(sub.properties ?? {})) {
					if (Object.hasOwn(value, key)) value[key] = cleanType(context, prop, value[key]);
				}
			}
			return value;
		}
		case "Object": {
			if (!isObjectNotArray(value)) return value;
			const additional = schema.additionalProperties;
			for (const key of Object.keys(value)) {
				if (schema.properties && Object.hasOwn(schema.properties, key)) {
					value[key] = cleanType(context, schema.properties[key], value[key]);
					continue;
				}
				const keep = additional === true || (additional && typeof additional === "object" && validatorCheck(additional, value[key], context));
				if (keep) {
					value[key] = cleanType(context, additional, value[key]);
					continue;
				}
				delete value[key];
			}
			return value;
		}
		case "Record": {
			if (!isObjectNotArray(value)) return value;
			const [pattern, sub] = Object.entries(schema.patternProperties ?? {})[0] ?? [];
			if (!sub) return value;
			const re = new RegExp(pattern);
			for (const key of Object.keys(value)) {
				if (re.test(key)) value[key] = cleanType(context, sub, value[key]);
				else if (schema.additionalProperties !== true) delete value[key];
			}
			return value;
		}
		case "Ref": {
			const target = resolveRef(context, schema);
			return target ? cleanType(context, target, value) : value;
		}
		case "Tuple":
			if (!Array.isArray(value)) return value;
			return value.slice(0, schema.items.length).map((item, i) => cleanType(context, schema.items[i], item));
		case "Union":
			for (const sub of schema.anyOf) {
				const clean = cleanType(context, sub, Clone(value));
				if (validatorCheck(sub, clean, context)) return clean;
			}
			return value;
		default:
			return value;
	}
}

export function Clean(...list) {
	const { context, schema, value } = args(list);
	return cleanType(context, schema, value);
}

// ---------------------------------------------------------------------------------------------
// Create
// ---------------------------------------------------------------------------------------------

function createType(context, schema) {
	if (schema && typeof schema === "object" && "default" in schema) {
		return typeof schema.default === "function" ? schema.default() : Clone(schema.default);
	}
	switch (kind(schema)) {
		case "Any":
		case "Unknown":
		case "Undefined":
		case "Void":
			return undefined;
		case "Array":
			return Array.from({ length: schema.minItems ?? 0 }, () => createType(context, schema.items));
		case "BigInt":
			return 0n;
		case "Boolean":
			return false;
		case "Enum":
			return schema.enum[0];
		case "Integer":
		case "Number":
			return typeof schema.minimum === "number" ? schema.minimum : typeof schema.exclusiveMinimum === "number" ? schema.exclusiveMinimum + 1 : 0;
		case "Intersect":
			return Object.assign({}, ...schema.allOf.map((sub) => createType(context, sub)));
		case "Literal":
			return schema.const;
		case "Null":
			return null;
		case "Object": {
			const result = {};
			for (const key of schema.required ?? []) result[key] = createType(context, schema.properties[key]);
			return result;
		}
		case "Record":
			return {};
		case "Ref": {
			const target = resolveRef(context, schema);
			return target ? createType(context, target) : undefined;
		}
		case "String":
			if ("pattern" in schema || "format" in schema) throw new Error("Strings with format or pattern constraints must specify default");
			return typeof schema.minLength === "number" ? " ".repeat(schema.minLength) : "";
		case "Never":
			throw new CreateError("Cannot create TNever types");
		case "Tuple":
			return schema.items.map((sub) => createType(context, sub));
		case "Union":
			return createType(context, schema.anyOf[0]);
		default:
			return undefined;
	}
}

export function Create(...list) {
	const { context, schema } = list.length >= 2 ? { context: list[0], schema: list[1] } : { context: undefined, schema: list[0] };
	return createType(context, schema);
}

// ---------------------------------------------------------------------------------------------
// Parse / Decode / Encode / Repair and misc helpers
// ---------------------------------------------------------------------------------------------

/** TypeBox 1.x Parse: returns the value when valid; with settings.correctiveParse runs Clone, Default, Convert, Clean, Assert. */
export const parseSettings = { correctiveParse: false };

export function Parse(...list) {
	const { context, schema, value } = args(list);
	if (validatorCheck(schema, value, context)) return value;
	if (parseSettings.correctiveParse) {
		let result = Clone(value);
		result = defaultType(context, schema, result);
		result = convertType(context, schema, result);
		result = cleanType(context, schema, result);
		if (!validatorCheck(schema, result, context)) throw new ParseError(result, validatorErrors(schema, result, context));
		return result;
	}
	throw new ParseError(value, validatorErrors(schema, value, context));
}

export function Decode(...list) {
	const { context, schema, value } = args(list);
	if (!validatorCheck(schema, value, context)) throw new DecodeError(value, validatorErrors(schema, value, context));
	return value;
}

export function DecodeUnsafe(...list) {
	const { value } = args(list);
	return value;
}

export function Encode(...list) {
	const { context, schema, value } = args(list);
	if (!validatorCheck(schema, value, context)) throw new EncodeError(value, validatorErrors(schema, value, context));
	return value;
}

export function EncodeUnsafe(...list) {
	const { value } = args(list);
	return value;
}

export function HasCodec() {
	return false;
}

export function Repair(...list) {
	const { context, schema, value } = args(list);
	if (validatorCheck(schema, value, context)) return value;
	let result = Clone(value);
	result = defaultType(context, schema, result);
	result = convertType(context, schema, result);
	result = cleanType(context, schema, result);
	if (validatorCheck(schema, result, context)) return result;
	return createType(context, schema);
}

export function Equal(left, right) {
	return deepEqual(left, right);
}

export function Hash(value) {
	const text = JSON.stringify(value, (_key, v) => (typeof v === "bigint" ? `${v}n` : v)) ?? "undefined";
	let hash = 0xcbf29ce484222325n;
	for (let i = 0; i < text.length; i++) {
		hash ^= BigInt(text.charCodeAt(i));
		hash = (hash * 0x100000001b3n) & 0xffffffffffffffffn;
	}
	return hash;
}

function pointerKeys(pointer) {
	if (pointer === "" || pointer === "/") return [];
	return pointer
		.replace(/^\//, "")
		.split("/")
		.map((part) => part.replace(/~1/g, "/").replace(/~0/g, "~"));
}

export const Pointer = {
	Format: (pointer) => pointerKeys(pointer),
	Get(value, pointer) {
		let current = value;
		for (const key of pointerKeys(pointer)) {
			if (current === null || typeof current !== "object") return undefined;
			current = current[key];
		}
		return current;
	},
	Has(value, pointer) {
		let current = value;
		for (const key of pointerKeys(pointer)) {
			if (current === null || typeof current !== "object" || !(key in current)) return false;
			current = current[key];
		}
		return true;
	},
	Set(value, pointer, update) {
		const keys = pointerKeys(pointer);
		let current = value;
		for (const key of keys.slice(0, -1)) {
			if (current[key] === undefined) current[key] = {};
			current = current[key];
		}
		if (keys.length > 0) current[keys.at(-1)] = update;
	},
	Delete(value, pointer) {
		const keys = pointerKeys(pointer);
		let current = value;
		for (const key of keys.slice(0, -1)) {
			if (current === null || typeof current !== "object") return;
			current = current[key];
		}
		if (current && typeof current === "object" && keys.length > 0) {
			if (Array.isArray(current)) current.splice(Number(keys.at(-1)), 1);
			else delete current[keys.at(-1)];
		}
	},
};

function diffValues(path, current, next, edits) {
	if (deepEqual(current, next)) return;
	if (isObjectNotArray(current) && isObjectNotArray(next)) {
		for (const key of Object.keys(next)) {
			const p = `${path}/${String(key).replace(/~/g, "~0").replace(/\//g, "~1")}`;
			if (!Object.hasOwn(current, key)) edits.push({ type: "insert", path: p, value: Clone(next[key]) });
			else diffValues(p, current[key], next[key], edits);
		}
		for (const key of Object.keys(current)) {
			if (!Object.hasOwn(next, key)) edits.push({ type: "delete", path: `${path}/${String(key).replace(/~/g, "~0").replace(/\//g, "~1")}` });
		}
		return;
	}
	edits.push({ type: "update", path, value: Clone(next) });
}

export function Diff(current, next) {
	const edits = [];
	diffValues("", current, next, edits);
	return edits;
}

export function Patch(current, edits) {
	let result = Clone(current);
	for (const edit of edits) {
		if (edit.path === "" && edit.type === "update") {
			result = Clone(edit.value);
			continue;
		}
		if (edit.type === "delete") Pointer.Delete(result, edit.path);
		else Pointer.Set(result, edit.path, Clone(edit.value));
	}
	return result;
}

export const Value = {
	Assert,
	Check,
	Clean,
	Clone,
	Convert,
	Create,
	Decode,
	Default,
	Diff,
	Encode,
	Equal,
	Errors,
	HasCodec,
	Hash,
	Parse,
	Patch,
	Pointer,
	Repair,
};

export default Value;
