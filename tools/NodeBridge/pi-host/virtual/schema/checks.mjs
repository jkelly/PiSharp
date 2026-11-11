// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/schema/src/checks.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export const ALWAYS = ()=>true;
export const NEVER = ()=>false;
const IS_STRING = (v)=>typeof v === "string";
const IS_NUMBER = (v)=>Number.isFinite(v);
const IS_INTEGER = (v)=>Number.isInteger(v);
const IS_BOOLEAN = (v)=>typeof v === "boolean";
const IS_NULL = (v)=>v === null;
export const IS_ARRAY = (v)=>Array.isArray(v);
const IS_BYTES = (v)=>v instanceof Uint8Array;
const IS_BIGINT = (v)=>typeof v === "bigint";
const IS_UNDEFINED = (v)=>v === undefined;
export const IS_OBJECT = (v)=>isObject(v);
export function isObject(v) {
    return typeof v === "object" && v !== null && !Array.isArray(v) && !(v instanceof Uint8Array);
}
export const TYPE_CHECKS = new Map([
    [
        "string",
        IS_STRING
    ],
    [
        "number",
        IS_NUMBER
    ],
    [
        "integer",
        IS_INTEGER
    ],
    [
        "boolean",
        IS_BOOLEAN
    ],
    [
        "null",
        IS_NULL
    ],
    [
        "array",
        IS_ARRAY
    ],
    [
        "object",
        IS_OBJECT
    ],
    [
        "bytes",
        IS_BYTES
    ],
    [
        "bigint",
        IS_BIGINT
    ],
    [
        "undefined",
        IS_UNDEFINED
    ]
]);
const leaves = new WeakMap([
    [
        IS_STRING,
        [
            1,
            undefined
        ]
    ],
    [
        IS_NUMBER,
        [
            2,
            undefined
        ]
    ],
    [
        IS_INTEGER,
        [
            3,
            undefined
        ]
    ],
    [
        IS_BOOLEAN,
        [
            4,
            undefined
        ]
    ],
    [
        IS_NULL,
        [
            5,
            undefined
        ]
    ],
    [
        ALWAYS,
        [
            6,
            undefined
        ]
    ]
]);
export function leafOf(check) {
    return leaves.get(check);
}
function leaf(code, arg, check) {
    leaves.set(check, [
        code,
        arg
    ]);
    return check;
}
class Bounds {
    min;
    max;
    xmin;
    xmax;
    constructor(min, max, xmin, xmax){
        this.min = min;
        this.max = max;
        this.xmin = xmin;
        this.xmax = xmax;
    }
}
function leafOk(code, x, arg) {
    switch(code){
        case 1:
            return typeof x === "string";
        case 2:
            return Number.isFinite(x);
        case 3:
            return Number.isInteger(x);
        case 4:
            return typeof x === "boolean";
        case 5:
            return x === null;
        case 7:
            return x === arg;
        case 8:
            {
                const list = arg;
                for(let i = 0; i < list.length; i++)if (list[i] === x) return true;
                return false;
            }
        case 9:
            return (typeBits(x) & arg) !== 0;
        case 10:
            return Number.isInteger(x) && inBounds(x, arg);
        case 11:
            return Number.isFinite(x) && inBounds(x, arg);
        case 12:
            return typeof x === "string" && lengthOk(x, arg.min, arg.max);
        case 13:
            return typeof x === "string" && arg.test(x);
        default:
            return true;
    }
}
function inBounds(x, b) {
    return x >= b.min && x <= b.max && x > b.xmin && x < b.xmax;
}
export const GUARD_NONE = 0;
export const GUARD_STRICT = 1;
export const GUARD_LOOSE = 2;
export const CODE = 15;
export const REQUIRED = 16;
export const INHERITED = 32;
export function and(parts) {
    switch(parts.length){
        case 0:
            return ALWAYS;
        case 1:
            return parts[0];
        case 2:
            {
                const [a, b] = parts;
                return (v)=>a(v) && b(v);
            }
        case 3:
            {
                const [a, b, c] = parts;
                return (v)=>a(v) && b(v) && c(v);
            }
        default:
            return (v)=>{
                for(let i = 0; i < parts.length; i++)if (!parts[i](v)) return false;
                return true;
            };
    }
}
export function indirect(slot) {
    return (v)=>slot.check(v);
}
export function scopedCheck(scope, uri, check) {
    return (v)=>{
        scope.push(uri);
        const ok = check(v);
        scope.pop();
        return ok;
    };
}
export function typeCheck(types) {
    if (types.length === 1) return (typeof types[0] === "string" ? TYPE_CHECKS.get(types[0]) : undefined) ?? NEVER;
    return maskCheck(typeMask(types));
}
const TYPE_BITS = new Map([
    [
        "string",
        1
    ],
    [
        "number",
        2
    ],
    [
        "integer",
        4
    ],
    [
        "boolean",
        8
    ],
    [
        "null",
        16
    ],
    [
        "array",
        32
    ],
    [
        "object",
        64
    ],
    [
        "bytes",
        128
    ],
    [
        "bigint",
        256
    ],
    [
        "undefined",
        512
    ]
]);
export function typeMask(types) {
    let mask = 0;
    for (const t of types)if (typeof t === "string") mask |= TYPE_BITS.get(t) ?? 0;
    return mask;
}
function typeBits(v) {
    switch(typeof v){
        case "string":
            return 1;
        case "number":
            return Number.isInteger(v) ? 6 : Number.isFinite(v) ? 2 : 0;
        case "boolean":
            return 8;
        case "object":
            return v === null ? 16 : Array.isArray(v) ? 32 : v instanceof Uint8Array ? 128 : 64;
        case "bigint":
            return 256;
        case "undefined":
            return 512;
        default:
            return 0;
    }
}
function maskCheck(mask) {
    return leaf(9, mask, (v)=>(typeBits(v) & mask) !== 0);
}
export function maskedAnyCheck(mask, checks) {
    if (checks.length === 0) return maskCheck(mask);
    const rest = checks.length === 1 ? checks[0] : anyCheck(checks);
    return (v)=>(typeBits(v) & mask) !== 0 || rest(v);
}
export function bytesCheck(min, max) {
    return (v)=>v instanceof Uint8Array && v.length >= min && v.length <= max;
}
export function codePoints(s) {
    let n = 0;
    for(let i = 0; i < s.length; i++, n++){
        const c = s.charCodeAt(i);
        if (c >= 0xd800 && c <= 0xdbff && i + 1 < s.length) {
            const next = s.charCodeAt(i + 1);
            if (next >= 0xdc00 && next <= 0xdfff) i++;
        }
    }
    return n;
}
function lengthOk(s, min, max) {
    const units = s.length;
    if (units < min) return false;
    if (units < 2 * min && codePoints(s) < min) return false;
    return units <= max || codePoints(s) <= max;
}
export function stringCheck(strict, min, max, re, format) {
    if (re !== undefined && min === 0 && max === Number.POSITIVE_INFINITY && format === undefined) {
        return strict ? leaf(13, re, (v)=>typeof v === "string" && re.test(v)) : (v)=>typeof v !== "string" || re.test(v);
    }
    if (re === undefined && format === undefined) {
        return strict ? leaf(12, new Bounds(min, max, 0, 0), (v)=>typeof v === "string" && lengthOk(v, min, max)) : (v)=>typeof v !== "string" || lengthOk(v, min, max);
    }
    return (v)=>{
        if (typeof v !== "string") return !strict;
        return lengthOk(v, min, max) && (re === undefined || re.test(v)) && (format === undefined || format(v));
    };
}
export function numberCheck(mode, min, max, xmin, xmax, multipleOf) {
    const check = (v)=>{
        if (typeof v !== "number" && typeof v !== "bigint") return mode === 0;
        if (mode === 3 ? typeof v !== "bigint" : mode !== 0 && !Number.isFinite(v)) return false;
        if (mode === 2 && !Number.isInteger(v)) return false;
        return v >= min && v <= max && v > xmin && v < xmax && (multipleOf === undefined || isMultiple(v, multipleOf));
    };
    const plain = typeof min === "number" && typeof max === "number" && typeof xmin === "number" && typeof xmax === "number";
    if (mode === 0 || mode === 3 || multipleOf !== undefined || !plain) return check;
    return leaf(mode === 2 ? 10 : 11, new Bounds(min, max, xmin, xmax), check);
}
export function isMultiple(v, d) {
    if (typeof v === "bigint" || typeof d === "bigint") {
        if (typeof v === "number" && !Number.isInteger(v) || typeof d === "number" && !Number.isInteger(d)) return false;
        const divisor = BigInt(d);
        return divisor !== 0n && BigInt(v) % divisor === 0n;
    }
    const q = v / d;
    if (Number.isInteger(q)) return true;
    if (!Number.isFinite(q)) return v % d === 0;
    const [vc, ve] = decimal(v);
    const [dc, de] = decimal(d);
    return ve >= de ? vc * 10n ** BigInt(ve - de) % dc === 0n : vc % (dc * 10n ** BigInt(de - ve)) === 0n;
}
function decimal(x) {
    const [mantissa, exponent = "0"] = Math.abs(x).toString().split("e");
    const point = mantissa.indexOf(".");
    const fraction = point < 0 ? 0 : mantissa.length - point - 1;
    return [
        BigInt(mantissa.replace(".", "")),
        Number(exponent) - fraction
    ];
}
export function guardObject(inner, strict) {
    return strict ? (v)=>isObject(v) && inner(v) : (v)=>!isObject(v) || inner(v);
}
export function guardArray(inner, strict) {
    return strict ? (v)=>Array.isArray(v) && inner(v) : (v)=>!Array.isArray(v) || inner(v);
}
export function declaredObject(entries, requiredCount, closed, guard) {
    const n = entries.length;
    return (v)=>{
        if (guard !== GUARD_NONE && !isObject(v)) return guard === GUARD_LOOSE;
        const o = v;
        let required = 0;
        let next = 0;
        for(const k in o){
            let i = next;
            if (entries[i] !== k) {
                i = 0;
                while(i < n && entries[i] !== k)i += 3;
                if (i === n) {
                    if (closed) return false;
                    continue;
                }
            }
            next = i + 3;
            const x = o[k];
            const f = entries[i + 2];
            if (x === undefined && !(f & REQUIRED)) continue;
            const c = f & CODE;
            if (c !== 0 ? !leafOk(c, x, entries[i + 1]) : !entries[i + 1](x)) return false;
            if (f & REQUIRED) required++;
        }
        return required === requiredCount;
    };
}
export function openObject(entries, keys, extraRequired, patterns, additional, guard) {
    const known = patterns.length > 0 || additional !== undefined ? new Set(keys) : undefined;
    return (v)=>{
        if (guard !== GUARD_NONE && !isObject(v)) return guard === GUARD_LOOSE;
        const o = v;
        for(let i = 0; i < entries.length; i += 3){
            const k = entries[i];
            const x = o[k];
            const f = entries[i + 2];
            if (x === undefined && !(f & REQUIRED && Object.hasOwn(o, k)) || f & INHERITED && !Object.hasOwn(o, k)) {
                if (f & REQUIRED) return false;
                continue;
            }
            const c = f & CODE;
            if (c !== 0 ? !leafOk(c, x, entries[i + 1]) : !entries[i + 1](x)) return false;
        }
        for(let i = 0; i < extraRequired.length; i++)if (!Object.hasOwn(o, extraRequired[i])) return false;
        if (known === undefined) return true;
        for (const k of Object.keys(o)){
            let matched = known.has(k);
            for(let i = 0; i < patterns.length; i++){
                if (patterns[i][0].test(k)) {
                    matched = true;
                    if (!patterns[i][1](o[k])) return false;
                }
            }
            if (!matched && additional !== undefined && !additional(o[k])) return false;
        }
        return true;
    };
}
export function propertyNamesCheck(check) {
    return (v)=>{
        for(const k in v)if (!check(k)) return false;
        return true;
    };
}
export function propertyCountCheck(min, max) {
    return (v)=>{
        let n = 0;
        for(const _ in v)n++;
        return n >= min && n <= max;
    };
}
export function dependentRequiredCheck(entries) {
    return (v)=>{
        const o = v;
        for (const [k, list] of entries)if (Object.hasOwn(o, k)) {
            for (const r of list)if (!Object.hasOwn(o, r)) return false;
        }
        return true;
    };
}
export function dependentSchemasCheck(entries) {
    return (v)=>{
        for (const [k, check] of entries)if (Object.hasOwn(v, k) && !check(v)) return false;
        return true;
    };
}
const NO_LEAF = [
    0,
    undefined
];
export function listCheck(guard, min, max, prefix, rest) {
    const n = prefix.length;
    const [code, arg] = (rest === undefined ? undefined : leafOf(rest)) ?? NO_LEAF;
    return (v)=>{
        if (!Array.isArray(v)) return guard === GUARD_LOOSE;
        const length = v.length;
        if (length < min || length > max) return false;
        const m = length < n ? length : n;
        for(let i = 0; i < m; i++)if (!prefix[i](v[i])) return false;
        if (rest === undefined) return true;
        if (code !== 0) {
            for(let i = n; i < length; i++)if (!leafOk(code, v[i], arg)) return false;
            return true;
        }
        for(let i = n; i < length; i++)if (!rest(v[i])) return false;
        return true;
    };
}
export function itemsCheck(prefix, rest) {
    return listCheck(GUARD_NONE, 0, Number.POSITIVE_INFINITY, prefix, rest);
}
export function lengthCheck(min, max) {
    return (v)=>v.length >= min && v.length <= max;
}
export function containsCheck(check, min, max) {
    return (v)=>{
        const list = v;
        let n = 0;
        for(let i = 0; i < list.length; i++)if (check(list[i]) && ++n > max) return false;
        return n >= min;
    };
}
export function uniqueCheck(v) {
    const list = v;
    for(let i = 1; i < list.length; i++)for(let j = 0; j < i; j++)if (deepEqual(list[i], list[j])) return false;
    return true;
}
export function enumCheck(values) {
    const primitives = values.filter((x)=>typeof x !== "object" || x === null);
    if (primitives.length === values.length && primitives.length <= 8) {
        return leaf(8, primitives, (v)=>{
            for(let i = 0; i < primitives.length; i++)if (primitives[i] === v) return true;
            return false;
        });
    }
    const set = new Set(primitives);
    const objects = values.filter((x)=>typeof x === "object" && x !== null);
    return (v)=>set.has(v) || typeof v === "object" && objects.some((o)=>deepEqual(v, o));
}
export function constCheck(value) {
    if (typeof value !== "object" || value === null) return leaf(7, value, (v)=>v === value);
    return (v)=>deepEqual(v, value);
}
export function anyCheck(checks) {
    return (v)=>{
        for(let i = 0; i < checks.length; i++)if (checks[i](v)) return true;
        return false;
    };
}
export function oneCheck(checks) {
    return (v)=>{
        let n = 0;
        for(let i = 0; i < checks.length; i++)if (checks[i](v) && ++n > 1) return false;
        return n === 1;
    };
}
export function notCheck(check) {
    return (v)=>!check(v);
}
export function ifCheck(cond, then, otherwise) {
    return (v)=>cond(v) ? then(v) : otherwise(v);
}
export function taggedCheck(key, tags, checks) {
    return (v)=>{
        if (!isObject(v)) return false;
        const tag = v[key];
        for(let i = 0; i < tags.length; i++)if (tags[i] === tag) return checks[i](v);
        return false;
    };
}
export function deepEqual(a, b) {
    if (a === b) return true;
    if (typeof a !== "object" || typeof b !== "object" || a === null || b === null) return false;
    const array = Array.isArray(a);
    if (array !== Array.isArray(b)) return false;
    if (array) {
        const x = a;
        const y = b;
        if (x.length !== y.length) return false;
        for(let i = 0; i < x.length; i++)if (!deepEqual(x[i], y[i])) return false;
        return true;
    }
    const x = a;
    const y = b;
    const keys = Object.keys(x);
    if (keys.length !== Object.keys(y).length) return false;
    for (const k of keys)if (!Object.hasOwn(y, k) || !deepEqual(x[k], y[k])) return false;
    return true;
}
