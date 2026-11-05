// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/validator.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { Coercer } from "./coerce.mjs";
import { Compiler, RESTART } from "./compiler.mjs";
import { Explainer } from "./explain.mjs";
export function compile(schema, options = NO_OPTIONS) {
    const check = compileCheck(schema, options);
    return options.coerce === true ? new CoercingValidator(schema, options, check) : new Validator(schema, options, check);
}
const NO_OPTIONS = Object.freeze({});
export class Validator {
    check;
    schema;
    options;
    explainer = undefined;
    constructor(schema, options, check){
        this.check = check;
        this.schema = schema;
        this.options = options;
    }
    errors(value) {
        if (this.check(value)) return [];
        this.explainer ??= new Explainer(compiler(this.schema, this.options), this.schema);
        return this.explainer.errors(value);
    }
}
export class CoercingValidator extends Validator {
    coercer = undefined;
    coerce(value) {
        this.coercer ??= new Coercer(compiler(this.schema, this.options), this.schema);
        return this.coercer.coerce(value);
    }
    parse(value) {
        const coerced = this.coerce(value);
        return this.check(coerced) ? {
            ok: true,
            value: coerced
        } : {
            ok: false,
            errors: this.errors(coerced)
        };
    }
}
function compileCheck(schema, options) {
    return compiler(schema, options).root();
}
function compiler(schema, options) {
    const plain = new Compiler(schema, options, false);
    try {
        plain.root();
        return plain;
    } catch (error) {
        if (error !== RESTART) throw error;
        return new Compiler(schema, options, true);
    }
}
