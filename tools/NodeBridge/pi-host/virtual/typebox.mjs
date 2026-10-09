// PiSharp native replacement for the `typebox` 1.x package (`Type` builder) as seen by Pi extensions.
// Builders return plain JSON Schema objects with the same enumerable shape TypeBox 1.3 produces, plus
// TypeBox's non-enumerable markers ('~kind', '~optional', '~readonly', '~unsafe'), so Type.Optional /
// Type.Readonly work inside Type.Object exactly like upstream. The 0.34-era symbols (Kind, OptionalKind,
// ReadonlyKind, Hint) are exported for source compatibility; TypeBox 1.x does not attach them to schemas,
// and neither does this module.
import { TypeCompiler, Compile, Validator } from "./typebox-compile.mjs";
import * as ValueModule from "./typebox-value.mjs";

export const Kind = Symbol.for("TypeBox.Kind");
export const OptionalKind = Symbol.for("TypeBox.Optional");
export const ReadonlyKind = Symbol.for("TypeBox.Readonly");
export const Hint = Symbol.for("TypeBox.Hint");
export const TransformKind = Symbol.for("TypeBox.Transform");

const HIDDEN = ["~kind", "~optional", "~readonly", "~unsafe"];

function hide(target, key, value) {
	Object.defineProperty(target, key, { value, enumerable: false, writable: true, configurable: true });
	return target;
}

function create(kind, schema, options) {
	const result = { ...schema, ...(options ?? {}) };
	if (kind !== undefined) hide(result, "~kind", kind);
	return result;
}

function clone(schema) {
	const result = { ...schema };
	for (const key of HIDDEN) if (Object.hasOwn(schema, key)) hide(result, key, schema[key]);
	return result;
}

function kindOf(schema) {
	return schema && typeof schema === "object" ? schema["~kind"] : undefined;
}

export function IsKind(value, kind) {
	return typeof value === "object" && value !== null && Object.hasOwn(value, "~kind") && value["~kind"] === kind;
}
export function IsOptional(value) {
	return typeof value === "object" && value !== null && value["~optional"] === true;
}
export function IsReadonly(value) {
	return typeof value === "object" && value !== null && value["~readonly"] === true;
}
export function IsSchema(value) {
	return typeof value === "boolean" || (typeof value === "object" && value !== null && !Array.isArray(value));
}
export function IsUnsafe(value) {
	return typeof value === "object" && value !== null && Object.hasOwn(value, "~unsafe");
}

export function Any(options) {
	return create("Any", {}, options);
}
export function Unknown(options) {
	return create("Unknown", {}, options);
}
export function Never(options) {
	return create("Never", { not: {} }, options);
}
export function Null(options) {
	return create("Null", { type: "null" }, options);
}
export function Undefined(options) {
	return create("Undefined", { type: "undefined" }, options);
}
export function Void(options) {
	return create("Void", { type: "void" }, options);
}
function _String(options) {
	return create("String", { type: "string" }, options);
}
function _Number(options) {
	return create("Number", { type: "number" }, options);
}
export function Integer(options) {
	return create("Integer", { type: "integer" }, options);
}
function _Boolean(options) {
	return create("Boolean", { type: "boolean" }, options);
}
function _BigInt(options) {
	return create("BigInt", { type: "bigint" }, options);
}
function _Symbol(options) {
	return create("Symbol", { type: "symbol" }, options);
}

export function Literal(value, options) {
	const type = typeof value === "bigint" ? "bigint" : typeof value;
	if (!["string", "number", "boolean", "bigint"].includes(type)) {
		throw new Error(`Invalid literal value: ${String(value)}`);
	}
	return create("Literal", { type, const: value }, options);
}

function _Array(items, options) {
	return create("Array", { type: "array", items }, options);
}

export function Tuple(items, options) {
	const minItems = items.length;
	return create("Tuple", { type: "array", additionalItems: false, items: [...items], minItems }, options);
}

export function Union(anyOf, options) {
	return create("Union", { anyOf: [...anyOf] }, options);
}

export function Intersect(allOf, options) {
	return create("Intersect", { allOf: [...allOf] }, options);
}

function requiredKeys(properties) {
	return Object.keys(properties).filter((key) => !IsOptional(properties[key]));
}

function _Object(properties = {}, options) {
	const required = requiredKeys(properties);
	const base = required.length > 0 ? { type: "object", required, properties } : { type: "object", properties };
	return create("Object", base, options);
}

function objectOptions(schema) {
	const options = { ...schema };
	delete options.type;
	delete options.required;
	delete options.properties;
	return options;
}

function propertiesOf(schema) {
	if (IsKind(schema, "Object") || (schema && schema.type === "object" && schema.properties)) return schema.properties ?? {};
	if (IsKind(schema, "Intersect")) return Object.assign({}, ...schema.allOf.map(propertiesOf));
	return {};
}

function keysOf(keys) {
	if (Array.isArray(keys)) return keys.map(String);
	if (IsKind(keys, "Literal")) return [String(keys.const)];
	if (IsKind(keys, "Union")) return keys.anyOf.flatMap(keysOf);
	if (keys && typeof keys === "object" && "const" in keys) return [String(keys.const)];
	if (keys && typeof keys === "object" && Array.isArray(keys.anyOf)) return keys.anyOf.flatMap(keysOf);
	if (keys && typeof keys === "object" && Array.isArray(keys.enum)) return keys.enum.map(String);
	return [String(keys)];
}

function withOptional(schema, optional) {
	const result = clone(schema);
	if (optional) hide(result, "~optional", true);
	else delete result["~optional"];
	return result;
}

export function Optional(schema, enable = true) {
	return withOptional(schema, enable !== false);
}

export function Readonly(schema, enable = true) {
	const result = clone(schema);
	if (enable !== false) hide(result, "~readonly", true);
	else delete result["~readonly"];
	return result;
}

export function ReadonlyOptional(schema) {
	return Readonly(Optional(schema));
}

export function Partial(schema, options) {
	const properties = Object.fromEntries(Object.entries(propertiesOf(schema)).map(([k, v]) => [k, withOptional(v, true)]));
	return _Object(properties, { ...objectOptions(schema), ...(options ?? {}) });
}

export function Required(schema, options) {
	const properties = Object.fromEntries(Object.entries(propertiesOf(schema)).map(([k, v]) => [k, withOptional(v, false)]));
	return _Object(properties, { ...objectOptions(schema), ...(options ?? {}) });
}

export function Pick(schema, keys, options) {
	const wanted = new Set(keysOf(keys));
	const properties = Object.fromEntries(Object.entries(propertiesOf(schema)).filter(([k]) => wanted.has(k)));
	return _Object(properties, { ...objectOptions(schema), ...(options ?? {}) });
}

export function Omit(schema, keys, options) {
	const unwanted = new Set(keysOf(keys));
	const properties = Object.fromEntries(Object.entries(propertiesOf(schema)).filter(([k]) => !unwanted.has(k)));
	return _Object(properties, { ...objectOptions(schema), ...(options ?? {}) });
}

export function KeyOf(schema, options) {
	return Union(
		Object.keys(propertiesOf(schema)).map((key) => Literal(key)),
		options,
	);
}

/** 0.34-style composite: merges object properties into a single object (not part of TypeBox 1.x). */
export function Composite(schemas, options) {
	const properties = {};
	for (const schema of schemas) Object.assign(properties, propertiesOf(schema));
	return _Object(properties, options);
}

const STRING_KEY = "^.*$";
const NUMBER_KEY = "^-?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?$";
const INTEGER_KEY = "^-?(?:0|[1-9][0-9]*)$";

export function Record(key, value, options) {
	if (IsKind(key, "Literal") || IsKind(key, "Union")) {
		const properties = Object.fromEntries(keysOf(key).map((k) => [k, value]));
		return _Object(properties, options);
	}
	const pattern = IsKind(key, "Integer") ? INTEGER_KEY : IsKind(key, "Number") ? NUMBER_KEY : typeof key?.pattern === "string" ? key.pattern : STRING_KEY;
	return create("Record", { type: "object", patternProperties: { [pattern]: value } }, options);
}

export function Enum(values, options) {
	let list;
	if (Array.isArray(values)) list = [...values];
	else
		list = Object.keys(values)
			.filter((key) => Number.isNaN(Number(key)))
			.map((key) => values[key]);
	return create("Enum", { enum: list }, options);
}

export function Unsafe(schema = {}) {
	const result = { ...schema };
	hide(result, "~unsafe", null);
	return result;
}

export function Ref(ref, options) {
	return create("Ref", { $ref: typeof ref === "string" ? ref : ref.$id }, options);
}

/** 0.34-style recursive type (`$id` + `$ref`), resolvable by this module's validator. */
let recursiveOrdinal = 0;
export function Recursive(callback, options = {}) {
	const $id = options.$id ?? `T${recursiveOrdinal++}`;
	const self = create("This", { $ref: $id });
	const body = callback(self);
	return create(kindOf(body) ?? "Recursive", { $id, ...body }, options);
}

function _Function(parameters, returnType, options) {
	return create("Function", { type: "function", parameters, returnType }, options);
}

export function Constructor(parameters, instanceType, options) {
	return create("Constructor", { type: "constructor", parameters, instanceType }, options);
}

function _Uint8Array(options) {
	return create("Uint8Array", { type: "Uint8Array" }, options);
}

function _RegExp(pattern, options) {
	return create("String", { type: "string", pattern: typeof pattern === "string" ? pattern : pattern.source }, options);
}

const guards = {
	IsAny: (v) => IsKind(v, "Any"),
	IsArray: (v) => IsKind(v, "Array"),
	IsBigInt: (v) => IsKind(v, "BigInt"),
	IsBoolean: (v) => IsKind(v, "Boolean"),
	IsConstructor: (v) => IsKind(v, "Constructor"),
	IsEnum: (v) => IsKind(v, "Enum"),
	IsFunction: (v) => IsKind(v, "Function"),
	IsInteger: (v) => IsKind(v, "Integer"),
	IsIntersect: (v) => IsKind(v, "Intersect"),
	IsLiteral: (v) => IsKind(v, "Literal"),
	IsNever: (v) => IsKind(v, "Never"),
	IsNull: (v) => IsKind(v, "Null"),
	IsNumber: (v) => IsKind(v, "Number"),
	IsObject: (v) => IsKind(v, "Object"),
	IsRecord: (v) => IsKind(v, "Record"),
	IsRef: (v) => IsKind(v, "Ref"),
	IsString: (v) => IsKind(v, "String"),
	IsSymbol: (v) => IsKind(v, "Symbol"),
	IsThis: (v) => IsKind(v, "This"),
	IsTuple: (v) => IsKind(v, "Tuple"),
	IsUndefined: (v) => IsKind(v, "Undefined"),
	IsUnion: (v) => IsKind(v, "Union"),
	IsUnknown: (v) => IsKind(v, "Unknown"),
	IsVoid: (v) => IsKind(v, "Void"),
};

export const Type = {
	Any,
	Array: _Array,
	BigInt: _BigInt,
	Boolean: _Boolean,
	Composite,
	Constructor,
	Enum,
	Function: _Function,
	Integer,
	Intersect,
	KeyOf,
	Literal,
	Never,
	Null,
	Number: _Number,
	Object: _Object,
	Omit,
	Optional,
	Partial,
	Pick,
	Readonly,
	ReadonlyOptional,
	Record,
	Recursive,
	Ref,
	RegExp: _RegExp,
	Required,
	String: _String,
	Symbol: _Symbol,
	Tuple,
	Uint8Array: _Uint8Array,
	Undefined,
	Union,
	Unknown,
	Unsafe,
	Void,
	IsKind,
	IsOptional,
	IsReadonly,
	IsSchema,
	IsUnsafe,
	...guards,
};

export const {
	IsAny,
	IsArray,
	IsBigInt,
	IsBoolean,
	IsConstructor,
	IsEnum,
	IsFunction,
	IsInteger,
	IsIntersect,
	IsLiteral,
	IsNever,
	IsNull,
	IsNumber,
	IsObject,
	IsRecord,
	IsRef,
	IsString,
	IsSymbol,
	IsThis,
	IsTuple,
	IsUndefined,
	IsUnion,
	IsUnknown,
	IsVoid,
} = guards;

export {
	_Array as Array,
	_BigInt as BigInt,
	_Boolean as Boolean,
	_Function as Function,
	_RegExp as RegExp,
	_Uint8Array as Uint8Array,
	_Number as Number,
	_Object as Object,
	_String as String,
	_Symbol as Symbol,
};

// TypeScript-only names (types are erased at runtime). Exported as undefined so that transpiled
// extensions which keep value-style imports of these names still link.
export const Static = undefined;
export const TSchema = undefined;
export const TObject = undefined;
export const TProperties = undefined;
export const TString = undefined;
export const TNumber = undefined;
export const TInteger = undefined;
export const TBoolean = undefined;
export const TArray = undefined;
export const TUnion = undefined;
export const TLiteral = undefined;
export const TOptional = undefined;
export const TUnsafe = undefined;
export const TAny = undefined;
export const TUnknown = undefined;
export const TNull = undefined;
export const TRecord = undefined;
export const TTuple = undefined;
export const TEnum = undefined;

// Convenience re-exports (TypeBox 0.34 exposed these from its root entry).
export { TypeCompiler, Compile, Validator };
export const Value = ValueModule.Value;

export default Type;
