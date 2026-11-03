// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/annotations.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { isObject } from "./checks.mjs";
export function newAnnotations() {
    return {
        props: null,
        allProps: false,
        items: 0,
        allItems: false,
        indexes: null
    };
}
function merge(a, b) {
    if (b.props !== null) for (const k of b.props)addProp(a, k);
    a.allProps ||= b.allProps;
    if (b.items > a.items) a.items = b.items;
    a.allItems ||= b.allItems;
    if (b.indexes !== null) for (const i of b.indexes)addIndex(a, i);
}
function addProp(a, key) {
    if (a.props === null) a.props = new Set();
    a.props.add(key);
}
function addIndex(a, index) {
    if (a.indexes === null) a.indexes = new Set();
    a.indexes.add(index);
}
export const ANNOTATING_TRUE = ()=>true;
export const ANNOTATING_FALSE = ()=>false;
export function indirectAnnotating(slot) {
    return (v, a)=>slot.check(v, a);
}
export function ignoreAnnotations(check) {
    return (v)=>check(v);
}
export function withFreshAnnotations(check) {
    return (v)=>check(v, newAnnotations());
}
export function scopedAnnotating(scope, uri, check) {
    return (v, a)=>{
        scope.push(uri);
        const ok = check(v, a);
        scope.pop();
        return ok;
    };
}
export function isolateAnnotations(check) {
    return (v, a)=>{
        const b = newAnnotations();
        if (!check(v, b)) return false;
        merge(a, b);
        return true;
    };
}
export function annotateAll(plain, parts) {
    return (v, a)=>{
        if (!plain(v)) return false;
        for(let i = 0; i < parts.length; i++)if (!parts[i](v, a)) return false;
        return true;
    };
}
export function annotateObject(props, patterns, additional) {
    const declared = new Set(props.map(([k])=>k));
    return (v, a)=>{
        if (!isObject(v)) return true;
        const seen = [];
        for (const [k, check] of props){
            if (!Object.hasOwn(v, k)) continue;
            if (!check(v[k])) return false;
            seen.push(k);
        }
        for (const k of Object.keys(v)){
            let matched = declared.has(k);
            for (const [re, check] of patterns){
                if (re.test(k)) {
                    if (!check(v[k])) return false;
                    matched = true;
                    seen.push(k);
                }
            }
            if (!matched && additional !== undefined) {
                if (!additional(v[k])) return false;
                seen.push(k);
            }
        }
        for (const k of seen)addProp(a, k);
        return true;
    };
}
export function annotateArray(prefix, rest, contains, minContains, maxContains) {
    return (v, a)=>{
        if (!Array.isArray(v)) return true;
        const m = Math.min(v.length, prefix.length);
        for(let i = 0; i < m; i++)if (!prefix[i](v[i])) return false;
        if (rest !== undefined) {
            for(let i = prefix.length; i < v.length; i++)if (!rest(v[i])) return false;
            a.allItems = true;
        }
        if (m > a.items) a.items = m;
        if (contains !== undefined) {
            let n = 0;
            for(let i = 0; i < v.length; i++){
                if (contains(v[i])) {
                    n++;
                    addIndex(a, i);
                }
            }
            if (n < minContains || n > maxContains) return false;
        }
        return true;
    };
}
export function annotateAny(branches, exactlyOne) {
    return (v, a)=>{
        let n = 0;
        for (const branch of branches){
            const b = newAnnotations();
            if (branch(v, b)) {
                n++;
                merge(a, b);
            }
        }
        return exactlyOne ? n === 1 : n > 0;
    };
}
export function annotateIf(cond, then, otherwise) {
    return (v, a)=>{
        const b = newAnnotations();
        if (cond(v, b)) {
            merge(a, b);
            return then === undefined || then(v, a);
        }
        return otherwise === undefined || otherwise(v, a);
    };
}
export function annotateDependent(key, check) {
    return (v, a)=>!isObject(v) || !Object.hasOwn(v, key) || check(v, a);
}
export function unevaluatedProperties(check) {
    return (v, a)=>{
        if (!isObject(v) || a.allProps) return true;
        for (const k of Object.keys(v))if (!(a.props?.has(k) ?? false) && !check(v[k])) return false;
        a.allProps = true;
        return true;
    };
}
export function unevaluatedItems(check) {
    return (v, a)=>{
        if (!Array.isArray(v) || a.allItems) return true;
        for(let i = a.items; i < v.length; i++)if (!(a.indexes?.has(i) ?? false) && !check(v[i])) return false;
        a.allItems = true;
        return true;
    };
}
