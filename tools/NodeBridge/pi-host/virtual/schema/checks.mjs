// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/checks.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export const ALWAYS = ()=>true;
export const NEVER = ()=>false;
const IS_STRING = (v)=>typeof v === "string";
const IS_NUMBER = (v)=>typeof v === "number";
const IS_INTEGER = (v)=>Number.isInteger(v);
const IS_BOOLEAN = (v)=>typeof v === "boolean";
const IS_NULL = (v)=>v === null;
export const IS_ARRAY = (v)=>Array.isArray(v);
const IS_BYTES = (v)=>v instanceof Uint8Array;
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
    ]
]);
export const LEAF_CODES = new Map([
    [
        IS_STRING,
        1
    ],
    [
        IS_NUMBER,
        2
    ],
    [
        IS_INTEGER,
        3
    ],
    [
        IS_BOOLEAN,
        4
    ],
    [
        IS_NULL,
        5
    ],
    [
        ALWAYS,
        6
    ]
]);
function leafOk(code, x) {
    switch(code){
        case 1:
            return typeof x === "string";
        case 2:
            return typeof x === "number";
        case 3:
            return Number.isInteger(x);
        case 4:
            return typeof x === "boolean";
        case 5:
            return x === null;
        default:
            return true;
    }
}
export const GUARD_NONE = 0;
export const GUARD_STRICT = 1;
export const GUARD_LOOSE = 2;
export const CODE = 7;
export const REQUIRED = 8;
export const INHERITED = 16;
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
    const checks = types.map((t)=>(typeof t === "string" ? TYPE_CHECKS.get(t) : undefined) ?? NEVER);
    if (checks.length === 1) return checks[0];
    return (v)=>{
        for(let i = 0; i < checks.length; i++)if (checks[i](v)) return true;
        return false;
    };
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
        return strict ? (v)=>typeof v === "string" && re.test(v) : (v)=>typeof v !== "string" || re.test(v);
    }
    if (re === undefined && format === undefined) {
        return strict ? (v)=>typeof v === "string" && lengthOk(v, min, max) : (v)=>typeof v !== "string" || lengthOk(v, min, max);
    }
    return (v)=>{
        if (typeof v !== "string") return !strict;
        return lengthOk(v, min, max) && (re === undefined || re.test(v)) && (format === undefined || format(v));
    };
}
export function numberCheck(mode, min, max, xmin, xmax, multipleOf) {
    return (v)=>{
        if (typeof v !== "number") return mode === 0;
        if (mode === 2 && !Number.isInteger(v)) return false;
        return v >= min && v <= max && v > xmin && v < xmax && (multipleOf === undefined || isMultiple(v, multipleOf));
    };
}
export function isMultiple(v, d) {
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
        for(const k in o){
            let i = 0;
            while(i < n && entries[i] !== k)i += 3;
            if (i === n) {
                if (closed) return false;
                continue;
            }
            const x = o[k];
            if (x === undefined) continue;
            const f = entries[i + 2];
            const c = f & CODE;
            if (c !== 0 ? !leafOk(c, x) : !entries[i + 1](x)) return false;
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
            if (x === undefined || f & INHERITED && !Object.hasOwn(o, k)) {
                if (f & REQUIRED) return false;
                continue;
            }
            const c = f & CODE;
            if (c !== 0 ? !leafOk(c, x) : !entries[i + 1](x)) return false;
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
export function listCheck(guard, min, max, prefix, rest) {
    const n = prefix.length;
    const code = rest === undefined ? 0 : LEAF_CODES.get(rest) ?? 0;
    return (v)=>{
        if (!Array.isArray(v)) return guard === GUARD_LOOSE;
        const length = v.length;
        if (length < min || length > max) return false;
        const m = length < n ? length : n;
        for(let i = 0; i < m; i++)if (!prefix[i](v[i])) return false;
        if (rest === undefined) return true;
        if (code !== 0) {
            for(let i = n; i < length; i++)if (!leafOk(code, v[i])) return false;
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
        return (v)=>{
            for(let i = 0; i < primitives.length; i++)if (primitives[i] === v) return true;
            return false;
        };
    }
    const set = new Set(primitives);
    const objects = values.filter((x)=>typeof x === "object" && x !== null);
    return (v)=>set.has(v) || typeof v === "object" && objects.some((o)=>deepEqual(v, o));
}
export function constCheck(value) {
    if (typeof value !== "object" || value === null) return (v)=>v === value;
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
