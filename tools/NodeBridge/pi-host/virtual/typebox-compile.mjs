// PiSharp native replacement for `typebox/compile` (TypeBox 1.x Compile/Validator) plus the TypeBox 0.34
// `TypeCompiler.Compile` spelling. "Compilation" binds a schema to the shared interpreter-style validator;
// there is no code generation (Code() returns a descriptive stub string).
import { check, errors } from "./typebox-validator.mjs";
import * as V from "./typebox-value.mjs";

export class Validator {
	#context;
	#schema;

	constructor(context, schema) {
		this.#context = context;
		this.#schema = schema;
	}

	IsAccelerated() {
		return false;
	}

	Context() {
		return this.#context ?? {};
	}

	Type() {
		return this.#schema;
	}

	Code() {
		return "/* PiSharp Node bridge: schemas are interpreted, not compiled */";
	}

	Check(value) {
		return check(this.#schema, value, this.#context);
	}

	Errors(value) {
		return errors(this.#schema, value, this.#context);
	}

	Parse(value) {
		return this.#context ? V.Parse(this.#context, this.#schema, value) : V.Parse(this.#schema, value);
	}

	Clean(value) {
		return this.#context ? V.Clean(this.#context, this.#schema, value) : V.Clean(this.#schema, value);
	}

	Convert(value) {
		return this.#context ? V.Convert(this.#context, this.#schema, value) : V.Convert(this.#schema, value);
	}

	Create() {
		return this.#context ? V.Create(this.#context, this.#schema) : V.Create(this.#schema);
	}

	Default(value) {
		return this.#context ? V.Default(this.#context, this.#schema, value) : V.Default(this.#schema, value);
	}

	Decode(value) {
		return this.#context ? V.Decode(this.#context, this.#schema, value) : V.Decode(this.#schema, value);
	}

	Encode(value) {
		return this.#context ? V.Encode(this.#context, this.#schema, value) : V.Encode(this.#schema, value);
	}
}

/** Compile(schema) or Compile(context, schema). */
export function Compile(...args) {
	const [context, schema] = args.length >= 2 ? args : [undefined, args[0]];
	return new Validator(context, schema);
}

export function Code(...args) {
	return Compile(...args).Code();
}

/** 0.34-style compiled check: Errors() is iterable and also offers First(). */
class TypeCheck extends Validator {
	Errors(value) {
		const list = super.Errors(value);
		Object.defineProperty(list, "First", { value: () => list[0], enumerable: false });
		return list;
	}
}

/** TypeBox 0.34 compatibility: TypeCompiler.Compile(schema, references?) -> { Check, Errors, ... }. */
export const TypeCompiler = {
	Compile(schema, references) {
		let context;
		if (Array.isArray(references)) {
			context = {};
			for (const ref of references) if (ref && typeof ref.$id === "string") context[ref.$id] = ref;
		} else {
			context = references;
		}
		return new TypeCheck(context, schema);
	},
	Code(schema) {
		return Code(schema);
	},
};

export default Compile;
