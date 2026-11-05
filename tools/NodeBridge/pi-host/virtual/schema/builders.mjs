// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/builders.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
export function str(options = {}) {
    return {
        type: "string",
        ...options
    };
}
export function num(options = {}) {
    return {
        type: "number",
        ...options
    };
}
export function int(options = {}) {
    return {
        type: "integer",
        ...options
    };
}
export function bool(options = {}) {
    return {
        type: "boolean",
        ...options
    };
}
export function nul(options = {}) {
    return {
        type: "null",
        ...options
    };
}
export function unknown(options = {}) {
    return {
        ...options
    };
}
export function lit(value, options = {}) {
    return {
        const: value,
        ...options
    };
}
export function oneOf(values, options = {}) {
    return {
        enum: values,
        ...options
    };
}
export function union(schemas, options = {}) {
    return {
        anyOf: schemas,
        ...options
    };
}
export function nullable(schema, options = {}) {
    return {
        anyOf: [
            schema,
            {
                type: "null"
            }
        ],
        ...options
    };
}
export function arr(items, options = {}) {
    return {
        type: "array",
        items,
        ...options
    };
}
export function tuple(items, options = {}) {
    return {
        type: "array",
        prefixItems: items,
        items: false,
        minItems: items.length,
        ...options
    };
}
export function map(value, options = {}) {
    return {
        type: "object",
        additionalProperties: value,
        ...options
    };
}
export function bytes(options = {}) {
    return {
        type: "bytes",
        ...options
    };
}
export class Optional {
    schema;
    constructor(schema){
        this.schema = schema;
    }
}
export function opt(schema) {
    return new Optional(schema);
}
export function obj(properties, options = {}) {
    const { open, ...annotations } = options;
    const props = {};
    const required = [];
    for (const [name, property] of Object.entries(properties)){
        if (property instanceof Optional) props[name] = property.schema;
        else {
            props[name] = property;
            required.push(name);
        }
    }
    return {
        type: "object",
        properties: props,
        ...required.length > 0 ? {
            required
        } : {},
        ...open === true ? {} : {
            additionalProperties: false
        },
        ...annotations
    };
}
