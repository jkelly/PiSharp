// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/openai-responses.lazy.ts.
// Ported to plain ESM for the PiSharp Node extension bridge (TypeScript types stripped mechanically; behaviour unchanged).
import { lazyApi } from "./lazy.mjs";
export const openAIResponsesApi = ()=>lazyApi(()=>import("../../ai-host/api.mjs"));
