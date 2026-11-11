// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/schema/src/coerce.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { isSchemaObject } from "./resolver.mjs";
import { sharedRegex } from "./shared.mjs";
export class Coercer {
    c;
    schema;
    entered = [];
    cuts = 0;
    memo = undefined;
    constructor(compiler, schema){
        this.c = compiler;
        this.schema = schema;
    }
    coerce(value) {
        const base = this.c.r.rootBase;
        const withoutNulls = this.dropOptionalNulls(value, this.schema, base);
        return this.coerceTypes(this.convertOnly(withoutNulls), this.schema, base);
    }
    convertOnly(value) {
        try {
            return this.convert(this.schema, this.c.r.rootBase, value);
        } finally{
            this.memo = undefined;
            this.cuts = 0;
        }
    }
    reenter(node, base, value, memoize) {
        const entered = this.entered;
        for(let i = 0; i < entered.length; i += 2){
            if (entered[i] === node && Object.is(entered[i + 1], value)) {
                this.cuts++;
                return value;
            }
        }
        const memo = memoize ? this.memo?.get(node) : undefined;
        if (memo?.has(value)) return memo.get(value);
        const cuts = this.cuts;
        entered.push(node, value);
        let result;
        try {
            result = this.convert(node, base, value);
        } finally{
            entered.length -= 2;
        }
        if (memoize && this.cuts === cuts) {
            this.memo ??= new Map();
            let values = this.memo.get(node);
            if (values === undefined) {
                values = new Map();
                this.memo.set(node, values);
            }
            values.set(value, result);
        }
        return result;
    }
    passes(node, base, value) {
        try {
            return this.c.check(node, this.baseOf(node, base))(value);
        } catch  {
            return undefined;
        }
    }
    baseOf(node, base) {
        return isSchemaObject(node) ? this.c.r.baseFor(node, base) : base;
    }
    dropOptionalNulls(value, schema, base) {
        if (!isSchemaObject(schema)) return value;
        const b = this.baseOf(schema, base);
        if (Array.isArray(value)) {
            return mapItems(value, (item, i)=>{
                const itemSchema = itemSchemaAt(schema, i);
                return itemSchema === undefined ? item : this.dropOptionalNulls(item, itemSchema, b);
            });
        }
        if (!isPlainObject(value)) return value;
        const properties = isSchemaObject(schema.properties) ? schema.properties : {};
        const patterns = isSchemaObject(schema.patternProperties) ? Object.entries(schema.patternProperties).map(([pattern, item])=>[
                sharedRegex(pattern),
                item
            ]) : [];
        const additional = isSchemaObject(schema.additionalProperties) ? schema.additionalProperties : undefined;
        if (patterns.length === 0 && additional === undefined && !isSchemaObject(schema.properties)) return value;
        const required = new Set(Array.isArray(schema.required) ? schema.required : []);
        let out;
        const set = (key, next)=>{
            if (next === (out ?? value)[key]) return;
            out ??= {
                ...value
            };
            out[key] = next;
        };
        for (const [key, property] of Object.entries(properties)){
            if (!Object.hasOwn(value, key)) continue;
            const isRef = isSchemaObject(property) && typeof property.$ref === "string";
            if (value[key] === null && !required.has(key) && !isRef && this.passes(property, b, null) === false) {
                out ??= {
                    ...value
                };
                delete out[key];
                continue;
            }
            set(key, this.dropOptionalNulls(value[key], property, b));
        }
        if (patterns.length > 0 || additional !== undefined) {
            for (const key of Object.keys(value)){
                let matched = Object.hasOwn(properties, key);
                for (const [pattern, item] of patterns){
                    if (!pattern.test(key)) continue;
                    matched = true;
                    set(key, this.dropOptionalNulls((out ?? value)[key], item, b));
                }
                if (!matched && additional !== undefined) set(key, this.dropOptionalNulls(value[key], additional, b));
            }
        }
        return out ?? value;
    }
    convert(node, base, value) {
        if (!isSchemaObject(node)) return value;
        const b = this.baseOf(node, base);
        if (typeof node.$ref === "string") {
            let target;
            try {
                target = this.c.r.resolve(node.$ref, b);
            } catch  {
                return value;
            }
            return isSchemaObject(target.node) ? this.reenter(target.node, target.base, value, true) : value;
        }
        return this.convertNode(node, b, value);
    }
    convertNode(node, b, value) {
        if (Object.hasOwn(node, "const")) return convertLiteral(node.const, value);
        if (Array.isArray(node.enum)) return this.convertEnum(node, node.enum, b, value);
        if (Array.isArray(node.anyOf)) return this.convertUnion(node, node.anyOf, b, value);
        if (Array.isArray(node.oneOf)) return this.convertUnion(node, node.oneOf, b, value);
        if (Array.isArray(node.allOf)) {
            let next = value;
            for (const member of node.allOf)next = this.convert(member, b, next);
            return this.convertMembers(node, b, next);
        }
        switch(node.type){
            case "string":
                return orValue(tryString(value), value);
            case "number":
                return orValue(tryNumber(value), value);
            case "integer":
                {
                    const result = tryNumber(value);
                    return result === undefined ? value : Math.trunc(result.value);
                }
            case "boolean":
                return orValue(tryBoolean(value), value);
            case "null":
                return orValue(tryNull(value), value);
            case "bigint":
                return orValue(tryBigInt(value), value);
            case "undefined":
                return orValue(tryUndefined(value), value);
            case "array":
                return this.convertArray(node, b, value);
            case "object":
                return this.convertObject(node, b, value);
            case undefined:
                if (node.properties !== undefined || node.patternProperties !== undefined) {
                    return this.convertObject(node, b, value);
                }
                if (node.items !== undefined || node.prefixItems !== undefined) return this.convertArray(node, b, value);
                return value;
            default:
                return value;
        }
    }
    convertEnum(node, values, base, value) {
        if (values.some((member)=>member === value)) return value;
        for (const member of values){
            const candidate = convertLiteral(member, value);
            if (candidate !== value && this.passes(node, base, candidate) === true) return candidate;
        }
        return value;
    }
    convertUnion(node, branches, base, value) {
        if (branches.some((branch)=>this.passes(branch, base, value) === true)) return this.convertMembers(node, base, value);
        for (const branch of branches){
            const candidate = this.convertMembers(node, base, this.convert(branch, base, value));
            if (this.passes(node, base, candidate) === true) return candidate;
        }
        return this.convertMembers(node, base, value);
    }
    convertMembers(node, base, value) {
        const declares = node.properties !== undefined || node.patternProperties !== undefined || node.additionalProperties !== undefined;
        return declares ? this.convertObject(node, base, value) : value;
    }
    convertArray(node, base, value) {
        const prefix = Array.isArray(node.items) ? node.items : Array.isArray(node.prefixItems) ? node.prefixItems : undefined;
        if (prefix !== undefined) {
            if (!Array.isArray(value)) return value;
            const rest = Array.isArray(node.items) ? undefined : node.items;
            return mapItems(value, (item, i)=>{
                const schema = i < prefix.length ? prefix[i] : rest;
                return schema === undefined ? item : this.convert(schema, base, item);
            });
        }
        if (Array.isArray(value)) {
            return node.items === undefined ? value : mapItems(value, (item)=>this.convert(node.items, base, item));
        }
        const item = node.items;
        return [
            isSchemaObject(item) ? this.reenter(item, base, value, false) : value
        ];
    }
    convertObject(node, base, value) {
        if (!isPlainObject(value)) return value;
        const properties = isSchemaObject(node.properties) ? node.properties : {};
        const patterns = isSchemaObject(node.patternProperties) ? Object.entries(node.patternProperties).map(([pattern, schema])=>[
                sharedRegex(pattern),
                schema
            ]) : [];
        const additional = isSchemaObject(node.additionalProperties) ? node.additionalProperties : undefined;
        let out;
        for (const key of Object.keys(value)){
            if (value[key] === undefined) continue;
            let next = value[key];
            let matched = Object.hasOwn(properties, key);
            if (matched) next = this.convert(properties[key], base, next);
            for (const [pattern, schema] of patterns){
                if (!pattern.test(key)) continue;
                matched = true;
                next = this.convert(schema, base, next);
            }
            if (!matched && additional !== undefined) next = this.convert(additional, base, next);
            if (next !== value[key]) {
                out ??= {
                    ...value
                };
                out[key] = next;
            }
        }
        return out ?? value;
    }
    coerceTypes(value, schema, base) {
        if (!isSchemaObject(schema)) return value;
        const b = this.baseOf(schema, base);
        let next = value;
        if (Array.isArray(schema.allOf)) for (const member of schema.allOf)next = this.coerceTypes(next, member, b);
        if (Array.isArray(schema.anyOf)) next = this.coerceUnion(next, schema.anyOf, b);
        if (Array.isArray(schema.oneOf)) next = this.coerceUnion(next, schema.oneOf, b);
        const types = typesOf(schema);
        const matchesUnionMember = types.length > 1 && types.some((type)=>matchesType(next, type));
        if (types.length > 0 && !matchesUnionMember) {
            for (const type of types){
                const candidate = coercePrimitive(next, type);
                if (candidate !== next) {
                    next = candidate;
                    break;
                }
            }
        }
        if (types.includes("object") && isPlainObject(next)) next = this.coerceProperties(next, schema, b);
        if (types.includes("array") && Array.isArray(next)) next = this.coerceItems(next, schema, b);
        return next;
    }
    coerceUnion(value, branches, base) {
        for (const branch of branches)if (this.passes(branch, base, value) === true) return value;
        for (const branch of branches){
            const coerced = this.coerceTypes(value, branch, base);
            if (this.passes(branch, base, coerced) === true) return coerced;
        }
        return value;
    }
    coerceProperties(value, schema, base) {
        const properties = isSchemaObject(schema.properties) ? schema.properties : {};
        const patterns = isSchemaObject(schema.patternProperties) ? Object.entries(schema.patternProperties).map(([pattern, item])=>[
                sharedRegex(pattern),
                item
            ]) : [];
        const additional = isSchemaObject(schema.additionalProperties) ? schema.additionalProperties : undefined;
        let out;
        const set = (key, next)=>{
            if (next === (out ?? value)[key]) return;
            out ??= {
                ...value
            };
            out[key] = next;
        };
        for (const [key, property] of Object.entries(properties)){
            if (key in value) set(key, this.coerceTypes(value[key], property, base));
        }
        for (const key of Object.keys(value)){
            let matched = Object.hasOwn(properties, key);
            for (const [pattern, item] of patterns){
                if (!pattern.test(key)) continue;
                matched = true;
                set(key, this.coerceTypes((out ?? value)[key], item, base));
            }
            if (!matched && additional !== undefined) set(key, this.coerceTypes(value[key], additional, base));
        }
        return out ?? value;
    }
    coerceItems(value, schema, base) {
        return mapItems(value, (item, i)=>{
            const itemSchema = itemSchemaAt(schema, i);
            return isSchemaObject(itemSchema) ? this.coerceTypes(item, itemSchema, base) : item;
        });
    }
}
function itemSchemaAt(schema, index) {
    if (Array.isArray(schema.items)) return schema.items[index];
    if (Array.isArray(schema.prefixItems) && index < schema.prefixItems.length) return schema.prefixItems[index];
    return schema.items;
}
function mapItems(items, map) {
    let out;
    for(let i = 0; i < items.length; i++){
        const next = map(items[i], i);
        if (next === items[i]) continue;
        out ??= items.slice();
        out[i] = next;
    }
    return out ?? items;
}
function orValue(result, value) {
    return result === undefined ? value : result.value;
}
function convertLiteral(literal, value) {
    if (literal === value) return value;
    const result = typeof literal === "string" ? tryString(value) : typeof literal === "number" ? tryNumber(value) : typeof literal === "boolean" ? tryBoolean(value) : typeof literal === "bigint" ? tryBigInt(value) : undefined;
    return result !== undefined && result.value === literal ? result.value : value;
}
function tryString(value) {
    if (typeof value === "bigint" || typeof value === "boolean" || isFiniteNumber(value)) return {
        value: String(value)
    };
    if (value === null) return {
        value: "null"
    };
    if (typeof value === "string") return {
        value
    };
    if (value === undefined) return {
        value: ""
    };
    return undefined;
}
const MAX_BIGINT = BigInt(Number.MAX_SAFE_INTEGER);
const MIN_BIGINT = BigInt(Number.MIN_SAFE_INTEGER);
function tryNumber(value) {
    if (typeof value === "bigint") return value <= MAX_BIGINT && value >= MIN_BIGINT ? {
        value: Number(value)
    } : undefined;
    if (typeof value === "boolean") return {
        value: value ? 1 : 0
    };
    if (isFiniteNumber(value)) return {
        value
    };
    if (value === null || value === undefined) return {
        value: 0
    };
    if (typeof value !== "string") return undefined;
    const coerced = +value;
    if (Number.isFinite(coerced)) return {
        value: coerced
    };
    const lowercase = value.toLowerCase();
    if (lowercase === "false") return {
        value: 0
    };
    if (lowercase === "true") return {
        value: 1
    };
    const big = bigIntOfString(value);
    return big !== undefined && big <= MAX_BIGINT && big >= MIN_BIGINT ? {
        value: Number(big)
    } : undefined;
}
function bigIntOfString(value) {
    if (/^-?(0|[1-9]\d*)n$/.test(value)) return BigInt(value.slice(0, -1));
    if (/^-?(0|[1-9]\d*)\.\d+$/.test(value)) return BigInt(value.split(".")[0]);
    if (/^-?(0|[1-9]\d*)$/.test(value)) return BigInt(value);
    return undefined;
}
function tryBigInt(value) {
    if (typeof value === "bigint") return {
        value
    };
    if (typeof value === "boolean") return {
        value: value ? 1n : 0n
    };
    if (isFiniteNumber(value)) return {
        value: BigInt(Math.trunc(value))
    };
    if (value === null || value === undefined) return {
        value: 0n
    };
    if (typeof value !== "string") return undefined;
    const big = bigIntOfString(value);
    if (big !== undefined) return {
        value: big
    };
    const lowercase = value.toLowerCase();
    return lowercase === "false" ? {
        value: 0n
    } : lowercase === "true" ? {
        value: 1n
    } : undefined;
}
function tryUndefined(value) {
    if (value === undefined || value === null || value === 0n || value === false || value === 0) return {
        value: undefined
    };
    if (typeof value !== "string") return undefined;
    const lowercase = value.toLowerCase();
    return lowercase === "undefined" || lowercase === "null" || value === "" || value === "0" ? {
        value: undefined
    } : undefined;
}
function tryBoolean(value) {
    if (typeof value === "bigint") return value === 0n ? {
        value: false
    } : value === 1n ? {
        value: true
    } : undefined;
    if (typeof value === "boolean") return {
        value
    };
    if (isFiniteNumber(value)) return value === 0 ? {
        value: false
    } : value === 1 ? {
        value: true
    } : undefined;
    if (value === null || value === undefined) return {
        value: false
    };
    if (typeof value !== "string") return undefined;
    const lowercase = value.toLowerCase();
    if (lowercase === "false" || value === "0") return {
        value: false
    };
    if (lowercase === "true" || value === "1") return {
        value: true
    };
    return undefined;
}
function tryNull(value) {
    if (typeof value === "bigint") return value === 0n ? {
        value: null
    } : undefined;
    if (typeof value === "boolean") return value === false ? {
        value: null
    } : undefined;
    if (isFiniteNumber(value)) return value === 0 ? {
        value: null
    } : undefined;
    if (value === null || value === undefined) return {
        value: null
    };
    if (typeof value !== "string") return undefined;
    const lowercase = value.toLowerCase();
    return lowercase === "undefined" || lowercase === "null" || value === "" || value === "0" ? {
        value: null
    } : undefined;
}
function isFiniteNumber(value) {
    return typeof value === "number" && Number.isFinite(value);
}
function typesOf(schema) {
    if (typeof schema.type === "string") return [
        schema.type
    ];
    if (Array.isArray(schema.type)) return schema.type.filter((type)=>typeof type === "string");
    return [];
}
function matchesType(value, type) {
    switch(type){
        case "number":
            return Number.isFinite(value);
        case "integer":
            return typeof value === "number" && Number.isInteger(value);
        case "boolean":
            return typeof value === "boolean";
        case "string":
            return typeof value === "string";
        case "null":
            return value === null;
        case "bigint":
            return typeof value === "bigint";
        case "undefined":
            return value === undefined;
        case "array":
            return Array.isArray(value);
        case "object":
            return isPlainObject(value);
        default:
            return false;
    }
}
function coercePrimitive(value, type) {
    switch(type){
        case "number":
        case "integer":
            {
                if (value === null) return 0;
                if (typeof value === "string" && value.trim() !== "") {
                    const parsed = Number(value);
                    if (type === "number" ? Number.isFinite(parsed) : Number.isInteger(parsed)) return parsed;
                }
                if (typeof value === "boolean") return value ? 1 : 0;
                return value;
            }
        case "boolean":
            if (value === null) return false;
            if (value === "true" || value === 1) return true;
            if (value === "false" || value === 0) return false;
            return value;
        case "string":
            if (value === null) return "";
            if (typeof value === "number" || typeof value === "boolean") return String(value);
            return value;
        case "null":
            return value === "" || value === 0 || value === false ? null : value;
        default:
            return value;
    }
}
function isPlainObject(value) {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}
