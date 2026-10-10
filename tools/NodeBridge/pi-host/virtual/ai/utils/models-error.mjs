// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/models-error.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { formatThrownValue } from "./diagnostics.mjs";
export class ModelsError extends Error {
    code;
    constructor(code, message, options){
        super(withCauseDetail(message, options?.cause), options);
        this.name = "ModelsError";
        this.code = code;
    }
}
function withCauseDetail(message, cause) {
    if (cause === undefined || cause === null) return message;
    const detail = formatThrownValue(cause).trim();
    if (!detail || message.includes(detail)) return message;
    return `${message}: ${detail}`;
}
