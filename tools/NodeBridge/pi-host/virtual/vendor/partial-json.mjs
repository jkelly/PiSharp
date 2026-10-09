// PiSharp native replacement for the `partial-json` npm package (parse(), Allow, errors), used by
// pi-ai's parseStreamingJson to read incomplete streamed tool-call arguments.

export const STR = 0b000000001;
export const NUM = 0b000000010;
export const ARR = 0b000000100;
export const OBJ = 0b000001000;
export const NULL = 0b000010000;
export const BOOL = 0b000100000;
export const NAN = 0b001000000;
export const INFINITY = 0b010000000;
export const _INFINITY = 0b100000000;
export const INF = INFINITY | _INFINITY;
export const SPECIAL = NULL | BOOL | INF | NAN;
export const ATOM = STR | NUM | SPECIAL;
export const COLLECTION = ARR | OBJ;
export const ALL = ATOM | COLLECTION;
export const Allow = { STR, NUM, ARR, OBJ, NULL, BOOL, NAN, INFINITY, _INFINITY, INF, SPECIAL, ATOM, COLLECTION, ALL };

export class PartialJSON extends Error {}
export class MalformedJSON extends Error {}

export function parse(jsonString, allowPartial = Allow.ALL) {
	if (typeof jsonString !== "string") {
		throw new TypeError(`expecting str, got ${typeof jsonString}`);
	}
	if (!jsonString.trim()) {
		throw new Error(`${jsonString} is empty`);
	}
	return parseJSON(jsonString.trim(), allowPartial);
}

function parseJSON(jsonString, allow) {
	const length = jsonString.length;
	let index = 0;

	const markPartialJSON = (msg) => {
		throw new PartialJSON(`${msg} at position ${index}`);
	};
	const throwMalformedError = (msg) => {
		throw new MalformedJSON(`${msg} at position ${index}`);
	};

	const parseLiteral = (literal, value, flag) => {
		if (
			jsonString.substring(index, index + literal.length) === literal ||
			(flag & allow && length - index < literal.length && literal.startsWith(jsonString.substring(index)))
		) {
			index += literal.length;
			return { ok: true, value };
		}
		return { ok: false };
	};

	const parseAny = () => {
		skipBlank();
		if (index >= length) markPartialJSON("Unexpected end of input");
		if (jsonString[index] === '"') return parseStr();
		if (jsonString[index] === "{") return parseObj();
		if (jsonString[index] === "[") return parseArr();
		for (const [literal, value, flag] of [
			["null", null, Allow.NULL],
			["true", true, Allow.BOOL],
			["false", false, Allow.BOOL],
			["Infinity", Number.POSITIVE_INFINITY, Allow.INFINITY],
			["-Infinity", Number.NEGATIVE_INFINITY, Allow._INFINITY],
			["NaN", Number.NaN, Allow.NAN],
		]) {
			if (literal === "-Infinity" && !(length - index > 1)) continue;
			const result = parseLiteral(literal, value, flag);
			if (result.ok) return result.value;
		}
		return parseNum();
	};

	const parseStr = () => {
		const start = index;
		let escape = false;
		index++;
		while (index < length && (jsonString[index] !== '"' || (escape && jsonString[index - 1] === "\\"))) {
			escape = jsonString[index] === "\\" ? !escape : false;
			index++;
		}
		if (jsonString.charAt(index) === '"') {
			try {
				return JSON.parse(jsonString.substring(start, ++index - Number(escape)));
			} catch (e) {
				throwMalformedError(String(e));
			}
		} else if (Allow.STR & allow) {
			try {
				return JSON.parse(`${jsonString.substring(start, index - Number(escape))}"`);
			} catch {
				return JSON.parse(`${jsonString.substring(start, jsonString.lastIndexOf("\\"))}"`);
			}
		}
		markPartialJSON("Unterminated string literal");
	};

	const parseObj = () => {
		index++;
		skipBlank();
		const obj = {};
		try {
			while (jsonString[index] !== "}") {
				skipBlank();
				if (index >= length && Allow.OBJ & allow) return obj;
				const key = parseStr();
				skipBlank();
				index++;
				try {
					const value = parseAny();
					Object.defineProperty(obj, key, { value, writable: true, enumerable: true, configurable: true });
				} catch (e) {
					if (Allow.OBJ & allow) return obj;
					throw e;
				}
				skipBlank();
				if (jsonString[index] === ",") index++;
			}
		} catch {
			if (Allow.OBJ & allow) return obj;
			markPartialJSON("Expected '}' at end of object");
		}
		index++;
		return obj;
	};

	const parseArr = () => {
		index++;
		const arr = [];
		try {
			while (jsonString[index] !== "]") {
				arr.push(parseAny());
				skipBlank();
				if (jsonString[index] === ",") index++;
			}
		} catch {
			if (Allow.ARR & allow) return arr;
			markPartialJSON("Expected ']' at end of array");
		}
		index++;
		return arr;
	};

	const parseNum = () => {
		if (index === 0) {
			if (jsonString === "-") throwMalformedError("Not sure what '-' is");
			try {
				return JSON.parse(jsonString);
			} catch (e) {
				if (Allow.NUM & allow) {
					try {
						return JSON.parse(jsonString.substring(0, jsonString.lastIndexOf("e")));
					} catch {}
				}
				throwMalformedError(String(e));
			}
		}
		const start = index;
		if (jsonString[index] === "-") index++;
		while (jsonString[index] && ",]}".indexOf(jsonString[index]) === -1) index++;
		if (index === length && !(Allow.NUM & allow)) markPartialJSON("Unterminated number literal");
		try {
			return JSON.parse(jsonString.substring(start, index));
		} catch (e) {
			if (jsonString.substring(start, index) === "-") markPartialJSON("Not sure what '-' is");
			try {
				return JSON.parse(jsonString.substring(start, jsonString.lastIndexOf("e")));
			} catch {
				throwMalformedError(String(e));
			}
		}
	};

	const skipBlank = () => {
		while (index < length && " \n\r\t".includes(jsonString[index])) index++;
	};

	return parseAny();
}

export default { parse, Allow, PartialJSON, MalformedJSON };
