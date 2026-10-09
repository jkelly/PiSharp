// PiSharp native JSON Schema validator shared by the typebox shims (typebox.mjs, typebox-compile.mjs,
// typebox-value.mjs). It follows TypeBox 1.x semantics: keyword evaluation order, error objects
// ({ keyword, schemaPath, instancePath, params, message }), en_US messages, and the 8-error cap.
// No third-party code is vendored.

export const settings = { maxErrors: 8 };

const formats = new Map();

export function setFormat(name, check) {
	formats.set(name, check);
}

const DATE_RE = /^(\d\d\d\d)-(\d\d)-(\d\d)$/;
const DAYS = [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
function isDate(str) {
	const m = DATE_RE.exec(str);
	if (!m) return false;
	const year = +m[1];
	const month = +m[2];
	const day = +m[3];
	const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
	return month >= 1 && month <= 12 && day >= 1 && day <= (month === 2 && leap ? 29 : DAYS[month]);
}
const TIME_RE = /^(\d\d):(\d\d):(\d\d(?:\.\d+)?)(z|([+-])(\d\d)(?::?(\d\d))?)?$/i;
function isTime(str, strictTimeZone = true) {
	const m = TIME_RE.exec(str);
	if (!m) return false;
	const hr = +m[1];
	const min = +m[2];
	const sec = +m[3];
	if (strictTimeZone && !m[4]) return false;
	return hr <= 23 && min <= 59 && sec < 61;
}
for (const [name, check] of Object.entries({
	email: (v) => /^[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/i.test(v),
	uuid: (v) => /^(?:urn:uuid:)?[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(v),
	uri: (v) => /^[a-z][a-z0-9+.-]*:[^\s]*$/i.test(v),
	"uri-reference": (v) => !/\s/.test(v),
	url: (v) => /^(?:https?|wss?|ftp):\/\/[^\s/$.?#].[^\s]*$/i.test(v),
	date: isDate,
	time: (v) => isTime(v),
	"date-time": (v) => {
		const parts = v.split(/t|\s/i);
		return parts.length === 2 && isDate(parts[0]) && isTime(parts[1]);
	},
	ipv4: (v) => /^(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)$/.test(v),
	ipv6: (v) => v.includes(":") && /^[0-9a-f:.]+$/i.test(v) && v.split("::").length <= 2,
	hostname: (v) => /^(?=.{1,253}\.?$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[-0-9a-z]{0,61}[0-9a-z])?)*\.?$/i.test(v),
	regex: (v) => {
		try {
			new RegExp(v, "u");
			return true;
		} catch {
			return false;
		}
	},
	"json-pointer": (v) => /^(?:\/(?:[^~/]|~0|~1)*)*$/.test(v),
	duration: (v) => /^P(?!$)((\d+Y)?(\d+M)?(\d+D)?(T(?=\d)(\d+H)?(\d+M)?(\d+S)?)?|(\d+W)?)$/.test(v),
})) {
	formats.set(name, check);
}

const regexCache = new Map();
function regex(pattern) {
	let re = regexCache.get(pattern);
	if (!re) {
		try {
			re = new RegExp(pattern, "u");
		} catch {
			re = new RegExp(pattern);
		}
		regexCache.set(pattern, re);
	}
	return re;
}

export const isObjectNotArray = (v) => typeof v === "object" && v !== null && !Array.isArray(v);
const isNumber = (v) => typeof v === "number" && Number.isFinite(v);

export function deepEqual(a, b) {
	if (a === b) return true;
	if (typeof a !== typeof b || a === null || b === null || typeof a !== "object") {
		return typeof a === "number" && typeof b === "number" && Number.isNaN(a) && Number.isNaN(b);
	}
	if (Array.isArray(a) !== Array.isArray(b)) return false;
	if (Array.isArray(a)) return a.length === b.length && a.every((x, i) => deepEqual(x, b[i]));
	const ka = Object.keys(a);
	const kb = Object.keys(b);
	return ka.length === kb.length && ka.every((k) => Object.hasOwn(b, k) && deepEqual(a[k], b[k]));
}

function typeMatches(type, value) {
	switch (type) {
		case "object":
			return isObjectNotArray(value);
		case "array":
			return Array.isArray(value);
		case "boolean":
			return typeof value === "boolean";
		case "integer":
			return isNumber(value) && Number.isInteger(value);
		case "number":
			return isNumber(value);
		case "null":
			return value === null;
		case "string":
			return typeof value === "string";
		case "bigint":
			return typeof value === "bigint";
		case "constructor":
		case "function":
			return typeof value === "function";
		case "symbol":
			return typeof value === "symbol";
		case "undefined":
		case "void":
			return value === undefined;
		default:
			return true;
	}
}

function escapePointer(key) {
	return String(key);
}

/** Resolution context: root schema plus optional external references (TypeBox Compile/Check context). */
export class Context {
	constructor(root, references) {
		this.root = root;
		this.references = references ?? {};
		this.ids = new Map();
		this.index(root);
		for (const value of Object.values(this.references)) this.index(value);
	}

	index(schema, seen = new Set()) {
		if (!schema || typeof schema !== "object" || seen.has(schema)) return;
		seen.add(schema);
		if (typeof schema.$id === "string") this.ids.set(schema.$id, schema);
		for (const value of Object.values(schema)) {
			if (value && typeof value === "object") this.index(value, seen);
		}
	}

	resolve(ref) {
		if (Object.hasOwn(this.references, ref)) return this.references[ref];
		if (this.ids.has(ref)) return this.ids.get(ref);
		if (ref === "#") return this.root;
		if (ref.startsWith("#/")) {
			let node = this.root;
			for (const raw of ref.slice(2).split("/")) {
				const key = decodeURIComponent(raw).replace(/~1/g, "/").replace(/~0/g, "~");
				node = node?.[key];
			}
			if (node !== undefined) return node;
		}
		const hash = ref.indexOf("#");
		if (hash > 0) {
			const base = this.ids.get(ref.slice(0, hash)) ?? this.references[ref.slice(0, hash)];
			if (base) return new Context(base, this.references).resolve(ref.slice(hash));
		}
		throw new Error(`Unable to dereference schema with $ref '${ref}'`);
	}
}

function hasObjectKeywords(s) {
	return (
		"required" in s ||
		"additionalProperties" in s ||
		"dependencies" in s ||
		"dependentRequired" in s ||
		"dependentSchemas" in s ||
		"patternProperties" in s ||
		"properties" in s ||
		"propertyNames" in s ||
		"minProperties" in s ||
		"maxProperties" in s
	);
}

/**
 * Evaluate `schema` against `value`. When `errors` is provided, errors are collected (TypeBox order);
 * otherwise evaluation short-circuits. Returns true when valid.
 */
export function validate(ctx, schema, value, errors, schemaPath = "#", instancePath = "") {
	if (errors && errors.length >= settings.maxErrors) return false;
	if (schema === true) return true;
	if (schema === false) {
		if (errors) push(errors, "boolean", schemaPath, instancePath, {});
		return false;
	}
	if (!schema || typeof schema !== "object") return true;
	let ok = true;
	const fail = () => {
		ok = false;
		return !errors; // stop when not collecting
	};

	if ("type" in schema) {
		const types = schema.type;
		const valid = Array.isArray(types) ? types.some((t) => typeMatches(t, value)) : typeMatches(types, value);
		if (!valid) {
			if (errors) push(errors, "type", schemaPath, instancePath, { type: types });
			if (fail()) return false;
		}
	}

	if (isObjectNotArray(value) && hasObjectKeywords(schema)) {
		const keys = Object.keys(value);
		if (Array.isArray(schema.required)) {
			const missing = schema.required.filter((key) => !Object.hasOwn(value, key));
			if (missing.length > 0) {
				if (errors) push(errors, "required", schemaPath, instancePath, { requiredProperties: missing });
				if (fail()) return false;
			}
		}
		if ("additionalProperties" in schema) {
			const known = new Set(schema.properties ? Object.keys(schema.properties) : []);
			const patterns = schema.patternProperties ? Object.keys(schema.patternProperties).map(regex) : [];
			const unknown = keys.filter((key) => !known.has(key) && !patterns.some((re) => re.test(key)));
			const ap = schema.additionalProperties;
			const failing = [];
			for (const key of unknown) {
				if (!validate(ctx, ap, value[key], errors, `${schemaPath}/additionalProperties`, `${instancePath}/${escapePointer(key)}`)) {
					failing.push(key);
					if (!errors) return false;
				}
			}
			if (failing.length > 0) {
				if (errors) push(errors, "additionalProperties", schemaPath, instancePath, { additionalProperties: failing });
				ok = false;
			}
		}
		if (schema.dependencies && typeof schema.dependencies === "object") {
			for (const [key, dep] of Object.entries(schema.dependencies)) {
				if (!Object.hasOwn(value, key)) continue;
				if (Array.isArray(dep)) {
					const missing = dep.filter((d) => !Object.hasOwn(value, d));
					if (missing.length > 0) {
						if (errors) push(errors, "dependencies", schemaPath, instancePath, { property: key, dependencies: missing });
						if (fail()) return false;
					}
				} else if (!validate(ctx, dep, value, errors, `${schemaPath}/dependencies/${key}`, instancePath)) {
					if (fail()) return false;
				}
			}
		}
		if (schema.dependentRequired && typeof schema.dependentRequired === "object") {
			for (const [key, deps] of Object.entries(schema.dependentRequired)) {
				if (!Object.hasOwn(value, key)) continue;
				const missing = deps.filter((d) => !Object.hasOwn(value, d));
				if (missing.length > 0) {
					if (errors) push(errors, "dependentRequired", schemaPath, instancePath, { property: key, dependencies: missing });
					if (fail()) return false;
				}
			}
		}
		if (schema.dependentSchemas && typeof schema.dependentSchemas === "object") {
			for (const [key, dep] of Object.entries(schema.dependentSchemas)) {
				if (Object.hasOwn(value, key) && !validate(ctx, dep, value, errors, `${schemaPath}/dependentSchemas/${key}`, instancePath)) {
					if (fail()) return false;
				}
			}
		}
		if (schema.patternProperties && typeof schema.patternProperties === "object") {
			for (const [pattern, sub] of Object.entries(schema.patternProperties)) {
				const re = regex(pattern);
				for (const key of keys) {
					if (re.test(key) && !validate(ctx, sub, value[key], errors, `${schemaPath}/patternProperties/${pattern}`, `${instancePath}/${escapePointer(key)}`)) {
						if (fail()) return false;
					}
				}
			}
		}
		if (schema.properties && typeof schema.properties === "object") {
			const required = new Set(Array.isArray(schema.required) ? schema.required : []);
			for (const [key, sub] of Object.entries(schema.properties)) {
				if (!Object.hasOwn(value, key)) continue;
				// TypeBox (exactOptionalPropertyTypes: false): an optional property may hold undefined.
				if (value[key] === undefined && !required.has(key)) continue;
				if (!validate(ctx, sub, value[key], errors, `${schemaPath}/properties/${key}`, `${instancePath}/${escapePointer(key)}`)) {
					if (fail()) return false;
				}
			}
		}
		if ("propertyNames" in schema) {
			const invalid = [];
			for (const key of keys) {
				if (!validate(ctx, schema.propertyNames, key, errors, `${schemaPath}/propertyNames`, `${instancePath}/${escapePointer(key)}`)) {
					invalid.push(key);
					if (!errors) return false;
				}
			}
			if (invalid.length > 0) {
				if (errors) push(errors, "propertyNames", schemaPath, instancePath, { propertyNames: invalid });
				ok = false;
			}
		}
		if (typeof schema.minProperties === "number" && keys.length < schema.minProperties) {
			if (errors) push(errors, "minProperties", schemaPath, instancePath, { limit: schema.minProperties });
			if (fail()) return false;
		}
		if (typeof schema.maxProperties === "number" && keys.length > schema.maxProperties) {
			if (errors) push(errors, "maxProperties", schemaPath, instancePath, { limit: schema.maxProperties });
			if (fail()) return false;
		}
	}

	if (Array.isArray(value)) {
		const prefix = Array.isArray(schema.prefixItems) ? schema.prefixItems.length : 0;
		if ("additionalItems" in schema && Array.isArray(schema.items)) {
			for (let i = schema.items.length; i < value.length; i++) {
				if (!validate(ctx, schema.additionalItems, value[i], errors, `${schemaPath}/additionalItems`, `${instancePath}/${i}`)) {
					if (fail()) return false;
				}
			}
		}
		let containsCount;
		if ("contains" in schema) {
			containsCount = value.filter((item) => validate(ctx, schema.contains, item, undefined)).length;
			const min = typeof schema.minContains === "number" ? schema.minContains : 1;
			if (containsCount < min && !(min === 0)) {
				if (errors) push(errors, "contains", schemaPath, instancePath, { minContains: min });
				if (fail()) return false;
			}
		}
		if ("items" in schema) {
			if (Array.isArray(schema.items)) {
				for (let i = 0; i < Math.min(schema.items.length, value.length); i++) {
					if (!validate(ctx, schema.items[i], value[i], errors, `${schemaPath}/items/${i}`, `${instancePath}/${i}`)) {
						if (fail()) return false;
					}
				}
			} else {
				for (let i = prefix; i < value.length; i++) {
					if (!validate(ctx, schema.items, value[i], errors, `${schemaPath}/items`, `${instancePath}/${i}`)) {
						if (fail()) return false;
					}
				}
			}
		}
		if (typeof schema.maxContains === "number" && "contains" in schema && containsCount > schema.maxContains) {
			if (errors) push(errors, "contains", schemaPath, instancePath, { maxContains: schema.maxContains });
			if (fail()) return false;
		}
		if (typeof schema.maxItems === "number" && value.length > schema.maxItems) {
			if (errors) push(errors, "maxItems", schemaPath, instancePath, { limit: schema.maxItems });
			if (fail()) return false;
		}
		if (typeof schema.minItems === "number" && value.length < schema.minItems) {
			if (errors) push(errors, "minItems", schemaPath, instancePath, { limit: schema.minItems });
			if (fail()) return false;
		}
		if (prefix > 0) {
			for (let i = 0; i < Math.min(prefix, value.length); i++) {
				if (!validate(ctx, schema.prefixItems[i], value[i], errors, `${schemaPath}/prefixItems/${i}`, `${instancePath}/${i}`)) {
					if (fail()) return false;
				}
			}
		}
		if (schema.uniqueItems === true) {
			const duplicates = [];
			for (let i = 0; i < value.length; i++) {
				for (let j = 0; j < i; j++) {
					if (deepEqual(value[i], value[j])) {
						if (!duplicates.some((d) => deepEqual(d, value[i]))) duplicates.push(value[i]);
						break;
					}
				}
			}
			if (duplicates.length > 0) {
				if (errors) push(errors, "uniqueItems", schemaPath, instancePath, { duplicateItems: duplicates });
				if (fail()) return false;
			}
		}
	}

	if (typeof value === "string") {
		const length = typeof schema.maxLength === "number" || typeof schema.minLength === "number" ? [...value].length : 0;
		if (typeof schema.maxLength === "number" && length > schema.maxLength) {
			if (errors) push(errors, "maxLength", schemaPath, instancePath, { limit: schema.maxLength });
			if (fail()) return false;
		}
		if (typeof schema.minLength === "number" && length < schema.minLength) {
			if (errors) push(errors, "minLength", schemaPath, instancePath, { limit: schema.minLength });
			if (fail()) return false;
		}
		if (typeof schema.format === "string") {
			const check = formats.get(schema.format);
			if (check && !check(value)) {
				if (errors) push(errors, "format", schemaPath, instancePath, { format: schema.format });
				if (fail()) return false;
			}
		}
		if (typeof schema.pattern === "string" && !regex(schema.pattern).test(value)) {
			if (errors) push(errors, "pattern", schemaPath, instancePath, { pattern: schema.pattern });
			if (fail()) return false;
		}
	}

	if (isNumber(value) || typeof value === "bigint") {
		if (typeof schema.exclusiveMaximum === "number" && !(value < schema.exclusiveMaximum)) {
			if (errors) push(errors, "exclusiveMaximum", schemaPath, instancePath, { comparison: "<", limit: schema.exclusiveMaximum });
			if (fail()) return false;
		}
		if (typeof schema.exclusiveMinimum === "number" && !(value > schema.exclusiveMinimum)) {
			if (errors) push(errors, "exclusiveMinimum", schemaPath, instancePath, { comparison: ">", limit: schema.exclusiveMinimum });
			if (fail()) return false;
		}
		if (typeof schema.maximum === "number" && !(value <= schema.maximum)) {
			if (errors) push(errors, "maximum", schemaPath, instancePath, { comparison: "<=", limit: schema.maximum });
			if (fail()) return false;
		}
		if (typeof schema.minimum === "number" && !(value >= schema.minimum)) {
			if (errors) push(errors, "minimum", schemaPath, instancePath, { comparison: ">=", limit: schema.minimum });
			if (fail()) return false;
		}
		if (typeof schema.multipleOf === "number") {
			const q = Number(value) / schema.multipleOf;
			if (!Number.isInteger(q) && Math.abs(q - Math.round(q)) > 1e-9) {
				if (errors) push(errors, "multipleOf", schemaPath, instancePath, { multipleOf: schema.multipleOf });
				if (fail()) return false;
			}
		}
	}

	if (typeof schema.$ref === "string") {
		const target = ctx.resolve(schema.$ref);
		if (!validate(ctx, target, value, errors, schemaPath, instancePath)) {
			if (fail()) return false;
		}
	}

	if ("const" in schema && !deepEqual(schema.const, value)) {
		if (errors) push(errors, "const", schemaPath, instancePath, { allowedValue: schema.const });
		if (fail()) return false;
	}

	if (Array.isArray(schema.enum) && !schema.enum.some((option) => deepEqual(option, value))) {
		if (errors) push(errors, "enum", schemaPath, instancePath, { allowedValues: schema.enum });
		if (fail()) return false;
	}

	if ("if" in schema) {
		const conditional = validate(ctx, schema.if, value, undefined);
		const branch = conditional ? "then" : "else";
		if (branch in schema && !validate(ctx, schema[branch], value, undefined)) {
			if (errors) push(errors, "if", schemaPath, instancePath, { failingKeyword: branch });
			if (fail()) return false;
		}
	}

	if ("not" in schema && validate(ctx, schema.not, value, undefined)) {
		if (errors) push(errors, "not", schemaPath, instancePath, {});
		if (fail()) return false;
	}

	if (Array.isArray(schema.allOf)) {
		for (let i = 0; i < schema.allOf.length; i++) {
			if (!validate(ctx, schema.allOf[i], value, errors, `${schemaPath}/allOf/${i}`, instancePath)) {
				if (fail()) return false;
			}
		}
	}

	if (Array.isArray(schema.anyOf)) {
		const passing = schema.anyOf.some((sub) => validate(ctx, sub, value, undefined));
		if (!passing) {
			if (errors) {
				for (let i = 0; i < schema.anyOf.length; i++) validate(ctx, schema.anyOf[i], value, errors, `${schemaPath}/anyOf/${i}`, instancePath);
				push(errors, "anyOf", schemaPath, instancePath, {});
			}
			if (fail()) return false;
		}
	}

	if (Array.isArray(schema.oneOf)) {
		const passing = [];
		schema.oneOf.forEach((sub, i) => {
			if (validate(ctx, sub, value, undefined)) passing.push(i);
		});
		if (passing.length !== 1) {
			if (errors) {
				if (passing.length === 0) {
					for (let i = 0; i < schema.oneOf.length; i++) validate(ctx, schema.oneOf[i], value, errors, `${schemaPath}/oneOf/${i}`, instancePath);
				}
				push(errors, "oneOf", schemaPath, instancePath, { passingSchemas: passing });
			}
			if (fail()) return false;
		}
	}

	return ok;
}

function message(keyword, params) {
	switch (keyword) {
		case "additionalProperties":
			return "must not have additional properties";
		case "anyOf":
			return "must match a schema in anyOf";
		case "boolean":
			return "schema is false";
		case "const":
			return "must be equal to constant";
		case "contains":
			return "must contain at least 1 valid item";
		case "dependencies":
		case "dependentRequired":
			return `must have properties ${params.dependencies.join(", ")} when property ${params.property} is present`;
		case "enum":
			return "must be equal to one of the allowed values";
		case "exclusiveMaximum":
		case "exclusiveMinimum":
		case "maximum":
		case "minimum":
			return `must be ${params.comparison} ${params.limit}`;
		case "format":
			return `must match format "${params.format}"`;
		case "if":
			return `must match "${params.failingKeyword}" schema`;
		case "maxItems":
			return `must not have more than ${params.limit} items`;
		case "maxLength":
			return `must not have more than ${params.limit} characters`;
		case "maxProperties":
			return `must not have more than ${params.limit} properties`;
		case "minItems":
			return `must not have fewer than ${params.limit} items`;
		case "minLength":
			return `must not have fewer than ${params.limit} characters`;
		case "minProperties":
			return `must not have fewer than ${params.limit} properties`;
		case "multipleOf":
			return `must be multiple of ${params.multipleOf}`;
		case "not":
			return "must not be valid";
		case "oneOf":
			return "must match exactly one schema in oneOf";
		case "pattern":
			return `must match pattern "${params.pattern}"`;
		case "propertyNames":
			return `property names ${params.propertyNames.join(", ")} are invalid`;
		case "required":
			return `must have required properties ${params.requiredProperties.join(", ")}`;
		case "type":
			return typeof params.type === "string" ? `must be ${params.type}` : `must be either ${params.type.join(" or ")}`;
		case "uniqueItems":
			return "must not have duplicate items";
		default:
			return "an unknown validation error occurred";
	}
}

function push(errors, keyword, schemaPath, instancePath, params) {
	if (errors.length >= settings.maxErrors) return;
	errors.push({ keyword, schemaPath, instancePath, params, message: message(keyword, params) });
}

export function check(schema, value, references) {
	return validate(new Context(schema, references), schema, value, undefined);
}

export function errors(schema, value, references) {
	const list = [];
	validate(new Context(schema, references), schema, value, list);
	return list;
}
